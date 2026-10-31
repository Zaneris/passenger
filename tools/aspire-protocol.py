#!/usr/bin/env python3
"""Writes the C# for Aspire's resource service protocol, src/Passenger.App/Aspire/Protocol/DashboardService.cs and
DashboardServiceGrpc.cs, from dashboard_service.proto beside them, with protoc and gRPC's C# plugin from Grpc.Tools.

The generated code is checked in, so no build runs protoc. The proto changes only with the Aspire version Passenger
speaks to; then `just aspire-protocol` and the two files' changes seen in review. Grpc.Tools is fetched once from
nuget.org into artifacts/aspire-protocol/, checked against the SHA-512 below, and never restored.
"""
import base64
import hashlib
import os
import platform
import stat
import subprocess
import sys
import urllib.request
import zipfile

VERSION = "2.85.0"
# The SHA-512 of nuget.org's Grpc.Tools.VERSION.nupkg, as NuGet's package cache records it (grpc.tools.VERSION.nupkg.sha512).
SHA512 = "2jKm1yx1zXk37Xjr0Y5lQljW0MomMoZjw2j2afOmD83WGZdE4jFJWWKVfM1ldp67GxoZ+deATvKYhpbJBWNp+g=="

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
APP = os.path.join(ROOT, "src", "Passenger.App")
PROTO = "Aspire/Protocol/dashboard_service.proto"
WORK = os.path.join(ROOT, "artifacts", "aspire-protocol")


def tools_folder():
    system = platform.system()
    machine = platform.machine().lower()
    if system == "Darwin":
        return "macosx_universal"
    if system == "Linux":
        return "linux_arm64" if machine in ("aarch64", "arm64") else "linux_x64"
    if system == "Windows":
        return "windows_x64"
    sys.exit(f"Grpc.Tools has no protoc for {system}")


def package():
    path = os.path.join(WORK, f"grpc.tools.{VERSION}.nupkg")
    if not os.path.exists(path):
        os.makedirs(WORK, exist_ok=True)
        url = f"https://api.nuget.org/v3-flatcontainer/grpc.tools/{VERSION}/grpc.tools.{VERSION}.nupkg"
        with urllib.request.urlopen(url) as answer:
            data = answer.read()
        with open(path + ".part", "wb") as part:
            part.write(data)
        os.replace(path + ".part", path)
    with open(path, "rb") as nupkg:
        actual = base64.b64encode(hashlib.sha512(nupkg.read()).digest()).decode()
    if actual != SHA512:
        os.remove(path)
        sys.exit(f"Grpc.Tools {VERSION} from nuget.org is not the package pinned here (SHA-512 {actual})")
    return path


def main():
    folder = tools_folder()
    suffix = ".exe" if platform.system() == "Windows" else ""
    tools = os.path.join(WORK, VERSION)
    with zipfile.ZipFile(package()) as nupkg:
        for name in nupkg.namelist():
            if name.startswith(f"tools/{folder}/") or name.startswith("build/native/include/"):
                nupkg.extract(name, tools)
    protoc = os.path.join(tools, "tools", folder, "protoc" + suffix)
    plugin = os.path.join(tools, "tools", folder, "grpc_csharp_plugin" + suffix)
    for program in (protoc, plugin):
        os.chmod(program, os.stat(program).st_mode | stat.S_IXUSR)
    out = os.path.join(APP, "Aspire", "Protocol")
    # A client-only Protobuf item's arguments, from the project's folder.
    subprocess.run([protoc, f"--csharp_out={out}", f"--plugin=protoc-gen-grpc={plugin}", f"--grpc_out={out}",
                    "--grpc_opt=no_server", f"--proto_path={os.path.join(tools, 'build', 'native', 'include')}",
                    "--proto_path=.", PROTO], cwd=APP, check=True)
    print(f"Wrote {os.path.relpath(out, ROOT)}/DashboardService.cs and DashboardServiceGrpc.cs")


if __name__ == "__main__":
    main()
