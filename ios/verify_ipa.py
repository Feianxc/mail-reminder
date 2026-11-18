"""Read back an actual iPhone binary package without using signing credentials."""
import hashlib
import json
import plistlib
import struct
import sys
from pathlib import Path
from zipfile import ZipFile

sys.stdout.reconfigure(encoding="utf-8")
ipa = Path(sys.argv[1])
with ZipFile(ipa) as archive:
    assert archive.testzip() is None, "Damaged IPA ZIP"
    base = "Payload/MailReminder.app/"
    info = plistlib.loads(archive.read(base + "Info.plist"))
    assert info["CFBundleIdentifier"] == "online.feian.mailreminder.ios"
    assert info["CFBundleShortVersionString"] == "1.3.0"
    assert info["CFBundleVersion"] == "130"
    assert info["CFBundleDisplayName"] == "邮件提醒助手"
    assert info["CFBundleSupportedPlatforms"] == ["iPhoneOS"]
    assert info["MinimumOSVersion"] == "16.0"
    assert "UIBackgroundModes" not in info
    binary = archive.read(base + info["CFBundleExecutable"])
    assert binary[:4] == b"\xcf\xfa\xed\xfe", "Expected 64-bit Mach-O"
    assert struct.unpack("<I", binary[4:8])[0] == 0x0100000C, "Expected arm64"
    assert base + "embedded.mobileprovision" not in archive.namelist()
report = {
    "status": "pass",
    "package": ipa.name,
    "bytes": ipa.stat().st_size,
    "sha256": hashlib.sha256(ipa.read_bytes()).hexdigest(),
    "bundleId": info["CFBundleIdentifier"],
    "version": info["CFBundleShortVersionString"],
    "minimumIOS": info["MinimumOSVersion"],
    "platform": "iPhoneOS arm64",
    "signing": "unsigned; sign on Windows using your own Apple ID",
    "backgroundDelivery": "not implemented; foreground reception only",
    "physicalDeviceInstallation": "not run",
}
Path(sys.argv[2]).write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
assert json.loads(Path(sys.argv[2]).read_text(encoding="utf-8")) == report
print(json.dumps(report, ensure_ascii=False, indent=2))
