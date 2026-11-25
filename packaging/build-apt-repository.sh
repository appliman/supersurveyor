#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

if [[ $# -lt 3 ]]; then
    printf 'Usage: %s OUTPUT_DIRECTORY REPOSITORY_URL PACKAGE [PACKAGE ...]\n' "$0" >&2
    exit 2
fi

output_directory=$1
repository_url=${2%/}
shift 2
case "$repository_url" in
    https://*|file:///*) ;;
    *)
        printf 'The repository URL must use HTTPS or an absolute local file URL.\n' >&2
        exit 2
        ;;
esac
if [[ "$repository_url" =~ [[:space:]] ]]; then
    printf 'Whitespace is not allowed in the repository URL.\n' >&2
    exit 2
fi

fingerprint=${APT_SIGNING_FINGERPRINT:?Set APT_SIGNING_FINGERPRINT to the OpenPGP key fingerprint.}
if [[ ! "$fingerprint" =~ ^([0-9A-Fa-f]{40}|[0-9A-Fa-f]{64})$ ]]; then
    printf 'An OpenPGP key fingerprint is required.\n' >&2
    exit 2
fi

mkdir -- "$output_directory"
for package in "$@"; do
    name=$(dpkg-deb -f "$package" Package)
    architecture=$(dpkg-deb -f "$package" Architecture)
    version=$(dpkg-deb -f "$package" Version)
    case "$name:$architecture" in
        supersurveyor:amd64|supersuveyor:amd64|supersuveyor:all) ;;
        *)
            printf 'Unexpected Debian package: %s (%s).\n' "$name" "$architecture" >&2
            exit 2
            ;;
    esac
    dpkg --validate-version "$version"
    destination="$output_directory/pool/main/s/$name/${name}_${version}_${architecture}.deb"
    install -Dm644 -- "$package" "$destination"
done

cd -- "$output_directory"
mkdir -p dists/stable/main/binary-amd64

package_index=dists/stable/main/binary-amd64/Packages
apt-ftparchive packages pool > "$package_index"
gzip -n -c "$package_index" > "$package_index.gz"

release=dists/stable/Release
apt-ftparchive \
    -o APT::FTPArchive::Release::Origin=SuperSurveyor \
    -o APT::FTPArchive::Release::Label=SuperSurveyor \
    -o APT::FTPArchive::Release::Suite=stable \
    -o APT::FTPArchive::Release::Codename=stable \
    -o APT::FTPArchive::Release::Architectures=amd64 \
    -o APT::FTPArchive::Release::Components=main \
    release dists/stable > Release.tmp
mv -- Release.tmp "$release"

gpg --batch --pinentry-mode loopback --passphrase-fd 3 \
    --local-user "$fingerprint" --output dists/stable/InRelease --clearsign "$release" \
    3<<<"${APT_SIGNING_PASSPHRASE:-}"
gpg --batch --pinentry-mode loopback --passphrase-fd 3 \
    --local-user "$fingerprint" --armor --output "$release.gpg" --detach-sign "$release" \
    3<<<"${APT_SIGNING_PASSPHRASE:-}"
gpg --batch --armor --export-options export-minimal --export "$fingerprint" > supersurveyor.asc
test -s supersurveyor.asc
cp -- supersurveyor.asc supersuveyor.asc

for name in supersurveyor supersuveyor; do
    cat > "$name.sources" <<EOF
Types: deb
URIs: $repository_url
Suites: stable
Components: main
Architectures: amd64
Signed-By: /etc/apt/keyrings/$name.asc
EOF
    printf '%s\n' "$fingerprint" > "$name.fingerprint"
done
