#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
xcodebuild -project MailReminder.xcodeproj -scheme MailReminder -configuration Debug \
  -sdk iphonesimulator -destination 'generic/platform=iOS Simulator' -derivedDataPath build-simulator \
  CODE_SIGNING_ALLOWED=YES CODE_SIGNING_REQUIRED=YES CODE_SIGN_IDENTITY="-" \
  CODE_SIGN_ENTITLEMENTS="$PWD/checks/Simulator.entitlements" build
DEVICE=$(xcrun simctl list devices available --json | python3 -c 'import json,sys; d=json.load(sys.stdin); print(next(v["udid"] for values in d["devices"].values() for v in values if "iPhone" in v["name"]))')
xcrun simctl boot "$DEVICE"
trap 'xcrun simctl shutdown "$DEVICE" || true' EXIT
python3 -c 'import subprocess,sys; subprocess.run(["xcrun","simctl","bootstatus",sys.argv[1],"-b"],check=True,timeout=180)' "$DEVICE"
xcrun simctl install "$DEVICE" build-simulator/Build/Products/Debug-iphonesimulator/MailReminder.app
xcrun simctl launch "$DEVICE" online.feian.mailreminder.ios
sleep 3
mkdir -p output
xcrun simctl io "$DEVICE" screenshot output/ios-first-launch.png
python3 -c 'import json,pathlib; p=pathlib.Path("output/simulator-result.json"); r={"status":"pass","scope":"simulator build, install, launch and screenshot; unbound first launch","physicalDevice":"not run"}; p.write_text(json.dumps(r,indent=2),encoding="utf-8"); assert json.loads(p.read_text())==r'
