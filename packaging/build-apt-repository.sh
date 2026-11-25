#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

if [[ $# != 3 ]]; then
    printf 'Usage: %s PACKAGE OUTPUT_DIRECTORY REPOSITORY_URL\n' "$0" >&2
    exit 2
fi

package=$(realpath -- "$1")
output_directory=$2
repository_url=${3%/}
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

if [[ "$(dpkg-deb -f "$package" Package)" != supersuveyor || \
      "$(dpkg-deb -f "$package" Architecture)" != amd64 ]]; then
    printf 'Expected an amd64 supersuveyor Debian package.\n' >&2
    exit 2
fi

fingerprint=${APT_SIGNING_FINGERPRINT:?Set APT_SIGNING_FINGERPRINT to the OpenPGP key fingerprint.}
if [[ ! "$fingerprint" =~ ^([0-9A-Fa-f]{40}|[0-9A-Fa-f]{64})$ ]]; then
    printf 'An OpenPGP key fingerprint is required.\n' >&2
    exit 2
fi

mkdir -- "$output_directory"
cd -- "$output_directory"
mkdir -p pool/main/s/supersuveyor dists/stable/main/binary-amd64
install -m644 "$package" pool/main/s/supersuveyor/

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
gpg --batch --armor --export-options export-minimal --export "$fingerprint" > supersuveyor.asc
test -s supersuveyor.asc

cat > supersuveyor.sources <<EOF
Types: deb
URIs: $repository_url
Suites: stable
Components: main
Architectures: amd64
Signed-By: /etc/apt/keyrings/supersuveyor.asc
EOF
printf '%s\n' "$fingerprint" > supersuveyor.fingerprint
