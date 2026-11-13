#!/bin/sh
# Passenger's debug relay (src/Passenger.DebugRelay/relay.c), the native piece a Debug app on a device loads as its
# runtime's profiler, built into artifacts/debug-relay/<rid>/, which the app carries under debugger/relay/<rid>/.
# Android's with Apple's clang and two packs of .NET's Android workload (its SDK pack's linker, its CoreCLR runtime
# pack's C runtime objects and stub libraries; no NDK, Android SDK or Java); iOS's with Xcode's SDKs. On macOS only.
#   tools/debug-relay.sh [android] [apple]
set -e
root=$(cd "$(dirname "$0")/.." && pwd)
src=$root/src/Passenger.DebugRelay/relay.c
out=$root/artifacts/debug-relay
packs=${DOTNET_ROOT:-$HOME/.dotnet}/packs
for target in ${@:-android apple}; do
  case $target in
    android)
      bin=$(ls -d "$packs"/Microsoft.Android.Sdk.Darwin/*/tools/Darwin/binutils/bin 2>/dev/null | tail -1)
      [ -n "$bin" ] || { echo "No Android workload in $packs: dotnet workload install android" >&2; exit 1; }
      for abi in arm64:aarch64 x64:x86_64; do
        rid=${abi%%:*}; triple=${abi##*:}
        libs=$(ls -d "$packs"/Microsoft.Android.Runtime.CoreCLR.*.android-$rid/*/runtimes/android-$rid/native 2>/dev/null | tail -1)
        [ -n "$libs" ] || { echo "No CoreCLR runtime pack for android-$rid in $packs: dotnet workload install android" >&2; exit 1; }
        mkdir -p "$out/android-$rid"
        clang -target $triple-linux-android24 -ffreestanding -fPIC -O2 -Wall -Werror -fvisibility=hidden -c "$src" -o "$out/android-$rid/relay.o"
        "$bin/ld" -shared -o "$out/android-$rid/libpassenger-debug.so" -soname libpassenger-debug.so "$libs/crtbegin_so.o" \
          "$out/android-$rid/relay.o" -L"$libs" -lc -ldl -llog "$libs/libclang_rt.builtins-$triple-android.a" "$libs/crtend_so.o" \
          -z noexecstack -z max-page-size=16384 --hash-style=gnu --build-id --no-undefined
        rm "$out/android-$rid/relay.o"
      done ;;
    apple)
      for t in iossimulator-arm64:arm64-apple-ios15.0-simulator:iphonesimulator ios-arm64:arm64-apple-ios15.0:iphoneos; do
        rid=$(echo $t | cut -d: -f1); triple=$(echo $t | cut -d: -f2); sdk=$(echo $t | cut -d: -f3)
        mkdir -p "$out/$rid"
        xcrun --sdk $sdk clang -target $triple -isysroot "$(xcrun --sdk $sdk --show-sdk-path)" -O2 -Wall -Werror -fvisibility=hidden \
          -dynamiclib -install_name @rpath/libpassenger-debug.dylib "$src" -o "$out/$rid/libpassenger-debug.dylib"
      done ;;
    *) echo "No relay target $target: android or apple" >&2; exit 2 ;;
  esac
done
ls -la "$out"/*/libpassenger-debug.*
