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
    private readonly Dictionary<(ServiceDescriptor, Type), object?> _instances = [];
    private readonly List<object> _owned = [];
    private readonly HashSet<object> _borrowed = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<(ServiceDescriptor, Type)> _activating = [];
    private bool _disposed;

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
        lock (_root._instances)
        {
            ObjectDisposedException.ThrowIf(_disposed || _root._disposed, this);
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
                        if (value is not null) _borrowed.Add(value);
                    }
                foreach (var descriptor in _descriptors.Where(d => Matches(d, elementType)))
                    values.Add(Resolve(descriptor, elementType));
                var result = Array.CreateInstance(elementType, values.Count);
                for (int i = 0; i < values.Count; i++) result.SetValue(values[i], i);
                return result;
            }
            return Borrow(serviceType);
        }
    }

    private object? Borrow(Type type)
    {
        var value = _fallback?.GetService(type);
        if (value is not null) _borrowed.Add(value);
        return value;
    }

    private static bool Matches(ServiceDescriptor descriptor, Type type) => !descriptor.IsKeyedService &&
        (descriptor.ServiceType == type || (type.IsConstructedGenericType && descriptor.ServiceType == type.GetGenericTypeDefinition()));

    private object? Resolve(ServiceDescriptor descriptor, Type type)
    {
        if (descriptor.ImplementationInstance is { } instance)
        {
            _borrowed.Add(instance);
            return instance;
        }
        var owner = descriptor.Lifetime == ServiceLifetime.Singleton ? _root : this;
        if (descriptor.Lifetime != ServiceLifetime.Transient && owner._instances.TryGetValue((descriptor, type), out var cached)) return cached;
        if (!owner._activating.Add((descriptor, type))) throw new InvalidOperationException($"Circular dependency resolving {type}.");
        try
        {
            object? value;
            if (descriptor.ImplementationFactory is { } factory) value = factory(owner);
            else
            {
                Type implementation = descriptor.ImplementationType!;
                if (implementation.IsGenericTypeDefinition) implementation = implementation.MakeGenericType(type.GenericTypeArguments);
                value = owner.Activate(implementation);
            }
            if (descriptor.Lifetime != ServiceLifetime.Transient) owner._instances.Add((descriptor, type), value);
            if (value is IDisposable or IAsyncDisposable && !owner._borrowed.Contains(value) && !owner._owned.Contains(value, ReferenceEqualityComparer.Instance))
                owner._owned.Add(value);
            return value;
        }
        finally { owner._activating.Remove((descriptor, type)); }
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
        lock (_root._instances)
        {
            ObjectDisposedException.ThrowIf(_root._disposed, this);
            return new BuilderServiceProvider(_root, _root._fallback?.GetService<IServiceScopeFactory>()?.CreateScope());
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        object[] owned;
        lock (_root._instances)
        {
            if (_disposed) return;
            _disposed = true;
            owned = _owned.AsEnumerable().Reverse().ToArray();
        }
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
