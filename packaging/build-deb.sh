#!/usr/bin/env bash
set -euo pipefail

if [[ $# != 2 ]]; then
    printf 'Usage: %s VERSION OUTPUT_DIRECTORY\n' "$0" >&2
    exit 2
fi

version=$1
output_directory=$2
if [[ ! "$version" =~ ^[0-9][0-9A-Za-z.+~:-]*$ || "$version" = *:* ]]; then
    printf 'Invalid package version.\n' >&2
    exit 2
fi
dpkg --validate-version "$version"

if [[ "$(dpkg --print-architecture)" != amd64 ]]; then
    printf 'The native Debian package must be built on amd64.\n' >&2
    exit 2
fi

packaging_directory=$(cd -- "$(dirname -- "$0")" && pwd)
source_project="$packaging_directory/../src/SuperSurveyor/SuperSurveyor.csproj"
build_directory=$(mktemp -d)
trap 'rm -rf -- "$build_directory"' EXIT
package_directory="$build_directory/package"
transition_directory="$build_directory/transition"
publish_directory="$build_directory/publish"

dotnet publish "$source_project" --configuration Release --runtime linux-x64 \
    -p:PublishAot=true --output "$publish_directory"

install -Dm755 "$publish_directory/supersurveyor" "$package_directory/usr/bin/supersurveyor"
install -Dm644 "$packaging_directory/supersurveyor.service" \
    "$package_directory/lib/systemd/system/supersurveyor.service"
for script in postinst prerm postrm; do
    install -Dm755 "$packaging_directory/$script" "$package_directory/DEBIAN/$script"
done

cat > "$package_directory/DEBIAN/control" <<EOF
Package: supersurveyor
Version: $version
Section: admin
Priority: optional
Architecture: amd64
Maintainer: SuperSurveyor maintainers <supersurveyor@users.noreply.github.com>
Depends: adduser, ca-certificates, init-system-helpers, libc6 (>= 2.36), libgcc-s1, libssl3 | libssl3t64, libstdc++6, systemd, tzdata
Breaks: supersuveyor (<< 0.2.0~)
Description: Linux system monitor with Free Mobile SMS alerts
 SuperSurveyor monitors CPU, memory and local disk usage and sends
 threshold alerts to the configured Free Mobile line.
EOF

mkdir -p -- "$transition_directory/DEBIAN" "$transition_directory/usr/bin"
ln -s supersurveyor "$transition_directory/usr/bin/supersuveyor"
cat > "$transition_directory/DEBIAN/control" <<EOF
Package: supersuveyor
Version: $version
Section: oldlibs
Priority: optional
Architecture: all
Maintainer: SuperSurveyor maintainers <supersurveyor@users.noreply.github.com>
Depends: supersurveyor (= $version)
Description: transitional package for supersurveyor
 SuperSurveyor was renamed to supersurveyor. This package upgrades
 existing installations and keeps the old command available.
EOF

mkdir -p -- "$output_directory"
dpkg-deb --root-owner-group --build "$package_directory" \
    "$output_directory/supersurveyor_${version}_amd64.deb"
dpkg-deb --root-owner-group --build "$transition_directory" \
    "$output_directory/supersuveyor_${version}_all.deb"
