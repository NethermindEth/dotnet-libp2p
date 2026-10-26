# Android Native Shim

`openssl_compat_shim.c` is the source for the prebuilt `libSystem.Security.Cryptography.Native.OpenSsl.so` files under `../jniLibs`.

The shim is intentionally shipped as a prebuilt Android native library because MAUI does not compile C sources in this sample project. Rebuild it with the Android NDK for each supported ABI, then replace the matching file in `../jniLibs/<abi>/`.

Example x86_64 build command:

```sh
$ANDROID_NDK_HOME/toolchains/llvm/prebuilt/linux-x86_64/bin/x86_64-linux-android21-clang \
  -shared -fPIC openssl_compat_shim.c \
  -o ../jniLibs/x86_64/libSystem.Security.Cryptography.Native.OpenSsl.so
```

Example arm64 build command:

```sh
$ANDROID_NDK_HOME/toolchains/llvm/prebuilt/linux-x86_64/bin/aarch64-linux-android21-clang \
  -shared -fPIC openssl_compat_shim.c \
  -o ../jniLibs/arm64-v8a/libSystem.Security.Cryptography.Native.OpenSsl.so
```
