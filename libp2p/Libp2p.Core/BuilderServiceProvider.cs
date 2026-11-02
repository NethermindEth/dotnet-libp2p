// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Libp2p.Core;

/// <summary>
/// Resolves builder registrations and their dependencies against the same container.
/// The fallback provider and anything obtained from it remain borrowed.
/// </summary>
internal sealed class BuilderServiceProvider : IServiceProvider, IServiceProviderIsService, IServiceScopeFactory, IServiceScope, IDisposable, IAsyncDisposable
{
    private readonly ServiceDescriptor[] _descriptors;
    private readonly IServiceProvider? _fallback;
    private readonly BuilderServiceProvider _root;
    private readonly IServiceScope? _fallbackScope;
    private readonly Dictionary<(ServiceDescriptor, Type), Lazy<object?>> _instances = [];
    private readonly List<object> _owned = [];
    private readonly HashSet<object> _borrowed = new(ReferenceEqualityComparer.Instance);
    [ThreadStatic]
    private static HashSet<(BuilderServiceProvider, ServiceDescriptor, Type)>? _activating;
    private int _activeResolutions;
    private TaskCompletionSource? _idle;
    private bool _disposed;
    private Task? _disposal;

    public BuilderServiceProvider(IEnumerable<ServiceDescriptor> descriptors, IServiceProvider? fallback)
    {
        _descriptors = descriptors.ToArray();
        _fallback = fallback;
        _root = this;
    }

    private BuilderServiceProvider(BuilderServiceProvider root, IServiceScope? fallbackScope)
    {
        _descriptors = root._descriptors;
        _root = root;
        _fallbackScope = fallbackScope;
        _fallback = fallbackScope?.ServiceProvider ?? root._fallback;
    }

    public IServiceProvider ServiceProvider => this;

    public object? GetService(Type serviceType)
    {
        BeginResolution();
        try
        {
            if (serviceType == typeof(IServiceProvider) || serviceType == typeof(IServiceProviderIsService)) return this;
            if (serviceType == typeof(IServiceScopeFactory)) return _root;
            var descriptors = _descriptors.Where(d => Matches(d, serviceType)).ToArray();
            if (descriptors.Length > 0) return Resolve(descriptors.LastOrDefault(d => d.ServiceType == serviceType) ?? descriptors[^1], serviceType);
            if (serviceType.IsGenericType && serviceType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                var elementType = serviceType.GenericTypeArguments[0];
                var values = new List<object?>();
                if (Borrow(serviceType) is System.Collections.IEnumerable supplied)
                    foreach (var value in supplied)
                    {
                        values.Add(value);
                        if (value is not null)
                            lock (_root._instances) _borrowed.Add(value);
                    }
                foreach (var descriptor in _descriptors.Where(d => Matches(d, elementType)))
                    values.Add(Resolve(descriptor, elementType));
                var result = Array.CreateInstance(elementType, values.Count);
                for (int i = 0; i < values.Count; i++) result.SetValue(values[i], i);
                return result;
            }
            return Borrow(serviceType);
        }
        finally { EndResolution(); }
    }

    private object? Borrow(Type type)
    {
        var value = _fallback?.GetService(type);
        if (value is not null)
            lock (_root._instances) _borrowed.Add(value);
        return value;
    }

    private static bool Matches(ServiceDescriptor descriptor, Type type) => !descriptor.IsKeyedService &&
        (descriptor.ServiceType == type || (type.IsConstructedGenericType && descriptor.ServiceType == type.GetGenericTypeDefinition()));

    private object? Resolve(ServiceDescriptor descriptor, Type type)
    {
        if (descriptor.ImplementationInstance is { } instance)
        {
            lock (_root._instances) _borrowed.Add(instance);
            return instance;
        }
        var owner = descriptor.Lifetime == ServiceLifetime.Singleton ? _root : this;
        if (descriptor.Lifetime == ServiceLifetime.Transient) return owner.Create(descriptor, type);
        Lazy<object?> cached;
        lock (_root._instances)
        {
            if (!owner._instances.TryGetValue((descriptor, type), out cached!))
            {
                cached = new(() => owner.Create(descriptor, type), LazyThreadSafetyMode.ExecutionAndPublication);
                owner._instances.Add((descriptor, type), cached);
            }
        }
        // A factory can call a peer that resolves other services. Never hold the container
        // lock across that callback; serialize creation only for this registration.
        try { return cached.Value; }
        catch
        {
            lock (_root._instances)
                if (owner._instances.TryGetValue((descriptor, type), out var current) && ReferenceEquals(current, cached))
                    owner._instances.Remove((descriptor, type));
            throw;
        }
    }

    private object? Create(ServiceDescriptor descriptor, Type type)
    {
        var activating = _activating ??= [];
        if (!activating.Add((this, descriptor, type))) throw new InvalidOperationException($"Circular dependency resolving {type}.");
        try
        {
            object? value;
            if (descriptor.ImplementationFactory is { } factory) value = factory(this);
            else
            {
                Type implementation = descriptor.ImplementationType!;
                if (implementation.IsGenericTypeDefinition) implementation = implementation.MakeGenericType(type.GenericTypeArguments);
                value = Activate(implementation);
            }
            if (value is IDisposable or IAsyncDisposable)
                lock (_root._instances)
                    if (!_borrowed.Contains(value) && !_owned.Contains(value, ReferenceEqualityComparer.Instance))
                        _owned.Add(value);
            return value;
        }
        finally { activating.Remove((this, descriptor, type)); }
    }

    private object Activate([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type type) =>
        ActivatorUtilities.CreateInstance(this, type);

    public bool IsService(Type serviceType) => serviceType == typeof(IServiceProvider) || serviceType == typeof(IServiceScopeFactory) ||
        serviceType == typeof(IServiceProviderIsService) ||
        (serviceType.IsGenericType && serviceType.GetGenericTypeDefinition() == typeof(IEnumerable<>)) ||
        _descriptors.Any(d => Matches(d, serviceType)) ||
        (_fallback?.GetService(typeof(IServiceProviderIsService)) as IServiceProviderIsService)?.IsService(serviceType) == true;

    public IServiceScope CreateScope()
    {
        BeginResolution();
        try
        {
            return new BuilderServiceProvider(_root, _root._fallback?.GetService<IServiceScopeFactory>()?.CreateScope());
        }
        finally { EndResolution(); }
    }

    private void BeginResolution()
    {
        lock (_root._instances)
        {
            ObjectDisposedException.ThrowIf(_disposed || _root._disposed, this);
            _activeResolutions++;
            if (_root != this) _root._activeResolutions++;
        }
    }

    private void EndResolution()
    {
        lock (_root._instances)
        {
            if (--_activeResolutions == 0) _idle?.TrySetResult();
            if (_root != this && --_root._activeResolutions == 0) _root._idle?.TrySetResult();
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        Task idle;
        TaskCompletionSource completion;
        lock (_root._instances)
        {
            if (_disposal is not null) return new(_disposal);
            _disposed = true;
            idle = _activeResolutions == 0 ? Task.CompletedTask :
                (_idle ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposal = completion.Task;
        }
        _ = DisposeOwnedAsync(idle, completion);
        return new(completion.Task);
    }

    private async Task DisposeOwnedAsync(Task idle, TaskCompletionSource completion)
    {
        try
        {
            await idle.ConfigureAwait(false);
            object[] owned;
            lock (_root._instances) owned = _owned.AsEnumerable().Reverse().ToArray();
            await DisposeValuesAsync(owned).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception ex) { completion.TrySetException(ex); }
    }

    private async Task DisposeValuesAsync(object[] owned)
    {
        List<Exception> failures = [];
        foreach (object value in owned)
        {
            try
            {
                if (value is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
                else ((IDisposable)value).Dispose();
            }
            catch (Exception ex) { failures.Add(ex); }
        }
        try
        {
            if (_fallbackScope is IAsyncDisposable asyncScope) await asyncScope.DisposeAsync();
            else _fallbackScope?.Dispose();
        }
        catch (Exception ex) { failures.Add(ex); }
        if (failures.Count > 0) throw new AggregateException(failures);
    }
}
