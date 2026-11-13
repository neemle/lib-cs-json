#!/bin/sh
set -eu

runtime_version=$1
output=$2
work_parent=$3
script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
mkdir -p "$work_parent" "$(dirname -- "$output")"
work=$(mktemp -d "$work_parent/runtime.XXXXXXXX")
trap 'find "$work" -depth -delete' EXIT HUP INT TERM

git clone --quiet --depth 1 --filter=blob:none --sparse --branch "v$runtime_version" \
    https://github.com/dotnet/runtime.git "$work/runtime"
git -C "$work/runtime" sparse-checkout set eng src/libraries/System.Text.Json src/libraries/Common \
    src/libraries/System.Private.CoreLib src/libraries/System.Runtime.InteropServices/gen \
    src/libraries/System.Text.RegularExpressions/gen src/libraries/Microsoft.NETCore.Platforms
git -C "$work/runtime" apply "$script_dir/JsonRuntime.patch"

# Load the SDK's linker tasks in the standalone runtime build.
sed -i 's/TaskFactory="TaskHostFactory"//g' "$work/runtime/eng/illink.targets"
printf '%s\n' '<Project><ItemGroup><ProjectReference Remove="@(ProjectReference)" /></ItemGroup></Project>' \
    > "$work/framework-refs.targets"
major=${runtime_version%%.*}
framework="net$major.0"
# Use the SDK linker instead of building the runtime repository's in-tree linker.
dotnet build "$work/runtime/src/libraries/System.Text.Json/src/System.Text.Json.csproj" \
    -f "$framework" -c Release -p:TargetFrameworks="$framework" \
    -p:CustomAfterMicrosoftCommonTargets="$work/framework-refs.targets" \
    -p:BuildProjectReferences=false -p:UseLocalTargetingRuntimePack=false \
    -p:DisableImplicitFrameworkReferences=false -p:_RequiresLiveILLink=false \
    -p:DotNetBuildSourceOnly=true -p:TreatWarningsAsErrors=true -p:NoWarn= -v:quiet
cp "$work/runtime/artifacts/bin/System.Text.Json/Release/$framework/System.Text.Json.dll" "$output"
