#!/bin/sh
# Passenger's debug relay (src/Passenger.DebugRelay/relay.c), the native piece a Debug app on a device loads as its
# runtime's profiler, built into artifacts/debug-relay/<rid>/, which the app carries under debugger/relay/<rid>/.
# Android's with Apple's clang and two packs of .NET's Android workload (its SDK pack's linker, its CoreCLR runtime
# pack's C runtime objects and stub libraries; no NDK, Android SDK or Java); iOS's and Mac Catalyst's with Xcode's SDKs.
# On macOS only.
#   tools/debug-relay.sh [android] [apple]
set -e
. "$(dirname "$0")/release-common.sh"
root=$(checkout_root)
src=$root/src/Passenger.DebugRelay/relay.c
out=$root/artifacts/debug-relay
# The Android workload is in the .NET that installed it: DOTNET_ROOT's, the dotnet on PATH's or ~/.dotnet, the first with it.
on_path=$(command -v dotnet || true)
[ -z "$on_path" ] || on_path=$(dirname "$(realpath "$on_path")")
for dotnet_root in ${DOTNET_ROOT:+"$DOTNET_ROOT"} ${on_path:+"$on_path"} "$HOME/.dotnet"; do
  packs=$dotnet_root/packs
  bin=$(ls -d "$packs"/Microsoft.Android.Sdk.Darwin/*/tools/Darwin/binutils/bin 2>/dev/null | tail -1)
  [ -z "$bin" ] || break
done
for target in ${@:-android apple}; do
  case $target in
    android)
      [ -n "$bin" ] || fail "no Android workload in DOTNET_ROOT's .NET, the dotnet on PATH's or ~/.dotnet: dotnet workload install android"
      for abi in arm64:aarch64 x64:x86_64; do
        rid=${abi%%:*}; triple=${abi##*:}
        libs=$(ls -d "$packs"/Microsoft.Android.Runtime.CoreCLR.*.android-$rid/*/runtimes/android-$rid/native 2>/dev/null | tail -1)
        [ -n "$libs" ] || fail "no CoreCLR runtime pack for android-$rid in $packs: dotnet workload install android"
        mkdir -p "$out/android-$rid"
        clang -target $triple-linux-android24 -ffreestanding -fPIC -O2 -Wall -Werror -fvisibility=hidden -c "$src" -o "$out/android-$rid/relay.o"
        "$bin/ld" -shared -o "$out/android-$rid/libpassenger-debug.so" -soname libpassenger-debug.so "$libs/crtbegin_so.o" \
          "$out/android-$rid/relay.o" -L"$libs" -lc -ldl -llog "$libs/libclang_rt.builtins-$triple-android.a" "$libs/crtend_so.o" \
          -z noexecstack -z max-page-size=16384 --hash-style=gnu --build-id --no-undefined
        rm "$out/android-$rid/relay.o"
      done ;;
    apple)
      for t in iossimulator-arm64:arm64-apple-ios15.0-simulator:iphonesimulator ios-arm64:arm64-apple-ios15.0:iphoneos \
          maccatalyst-arm64:arm64-apple-ios15.0-macabi:macosx maccatalyst-x64:x86_64-apple-ios15.0-macabi:macosx; do
        rid=$(echo $t | cut -d: -f1); triple=$(echo $t | cut -d: -f2); sdk=$(echo $t | cut -d: -f3)
        mkdir -p "$out/$rid"
        xcrun --sdk $sdk clang -target $triple -isysroot "$(xcrun --sdk $sdk --show-sdk-path)" -O2 -Wall -Werror -fvisibility=hidden \
          -dynamiclib -install_name @rpath/libpassenger-debug.dylib "$src" -o "$out/$rid/libpassenger-debug.dylib"
      done ;;
    *) echo "No relay target $target: android or apple" >&2; exit 2 ;;
  esac
done
ls -la "$out"/*/libpassenger-debug.*
