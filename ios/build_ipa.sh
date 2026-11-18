#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
command -v xcodegen >/dev/null || { echo 'XcodeGen is required.' >&2; exit 1; }
xcodegen generate
xcodebuild -version
xcodebuild -project MailReminder.xcodeproj -scheme MailReminder -configuration Release \
  -sdk iphoneos -destination 'generic/platform=iOS' -derivedDataPath build \
  CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO CODE_SIGN_IDENTITY="" build

APP="$PWD/build/Build/Products/Release-iphoneos/MailReminder.app"
test -x "$APP/MailReminder"
xcrun lipo "$APP/MailReminder" -verify_arch arm64
STAGE=$(mktemp -d "$PWD/build/package.XXXXXX")
trap 'rm -rf "$STAGE"' EXIT
mkdir "$STAGE/Payload"
cp -R "$APP" "$STAGE/Payload/"
mkdir -p output
IPA="$PWD/output/邮件提醒助手-iOS-1.3.0-未签名.ipa"
(cd "$STAGE" && /usr/bin/zip -qry "$IPA" Payload)
python3 verify_ipa.py "$IPA" output/build-result.json
echo "IPA generated at $IPA"
