from __future__ import annotations

# Windows prerequisites: Android SDK Build-Tools 35.0.0, Android SDK Platform 35,
# and a JDK providing java, javac and keytool. Set ANDROID_SDK_ROOT (or
# ANDROID_HOME), or install the SDK under %LOCALAPPDATA%\Android\Sdk.
# Set JAVA_HOME to the JDK directory, or put its bin directory on PATH.

import hashlib
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import subprocess
import sys
import tempfile
import zipfile

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
sys.stderr.reconfigure(encoding="utf-8", errors="replace")

ROOT = Path(__file__).resolve().parent
PROJECT = ROOT.parent
EVIDENCE = ROOT / ".build"
WORK = Path(tempfile.mkdtemp(prefix="feian-mail-android-"))
KEYS = ROOT / ".signing"
VERSION = "1.3.1"
VERSION_CODE = 131
OUTPUT = PROJECT / "release" / f"mail-reminder-android-{VERSION}.apk"
LOG: list[dict] = []


def run(label: str, arguments: list[str | Path]) -> str:
    process = subprocess.run([str(value) for value in arguments], cwd=WORK, capture_output=True,
                             encoding="utf-8", errors="replace")
    LOG.append({"step": label, "exitCode": process.returncode, "output": process.stdout + process.stderr})
    print(f"{label} exit={process.returncode}")
    if process.returncode:
        print(process.stdout + process.stderr)
        raise RuntimeError(f"{label} failed with exit {process.returncode}")
    return process.stdout + process.stderr


def main() -> None:
    sdk_directory = os.environ.get("ANDROID_SDK_ROOT") or os.environ.get("ANDROID_HOME")
    if not sdk_directory:
        local_app_data = os.environ.get("LOCALAPPDATA")
        if not local_app_data:
            raise RuntimeError("Set ANDROID_SDK_ROOT or ANDROID_HOME to your Android SDK directory")
        sdk_directory = str(Path(local_app_data) / "Android" / "Sdk")
    SDK = Path(sdk_directory).expanduser().resolve()
    TOOLS = SDK / "build-tools" / "35.0.0"
    PLATFORM = SDK / "platforms" / "android-35" / "android.jar"
    for path in [PLATFORM, TOOLS / "aapt.exe", TOOLS / "aapt2.exe", TOOLS / "zipalign.exe",
                 TOOLS / "lib/d8.jar", TOOLS / "lib/apksigner.jar"]:
        if not path.is_file():
            raise FileNotFoundError(f"Missing Android SDK component: {path}. Install Android SDK Build-Tools "
                                    "35.0.0 and Android SDK Platform 35, then check ANDROID_SDK_ROOT/ANDROID_HOME.")
    java_home = os.environ.get("JAVA_HOME")
    java_tools = {}
    for tool in ("java", "javac", "keytool"):
        executable = str(Path(java_home) / "bin" / f"{tool}.exe") if java_home else shutil.which(tool)
        if not executable or not Path(executable).is_file():
            raise FileNotFoundError(f"JDK tool {tool} was not found. Set JAVA_HOME to a complete JDK "
                                    "directory or put java, javac and keytool on PATH.")
        java_tools[tool] = Path(executable).resolve()
    JAVA, JAVAC, KEYTOOL = (java_tools[tool] for tool in ("java", "javac", "keytool"))
    previous_apk = PROJECT / "release" / "mail-reminder-android-1.3.0.apk"
    previous_hash = hashlib.sha256(previous_apk.read_bytes()).hexdigest() if previous_apk.is_file() else None
    WORK.mkdir(exist_ok=True)
    EVIDENCE.mkdir(exist_ok=True)
    shutil.copytree(ROOT / "res", WORK / "res", dirs_exist_ok=True)
    shutil.copytree(ROOT / "src", WORK / "src", dirs_exist_ok=True)
    shutil.copyfile(ROOT / "AndroidManifest.xml", WORK / "AndroidManifest.xml")
    staged_platform = WORK / "android.jar"
    shutil.copyfile(PLATFORM, staged_platform)
    KEYS.mkdir(exist_ok=True)
    OUTPUT.parent.mkdir(exist_ok=True)
    classes, dex, generated = (WORK / part for part in ("classes", "dex", "generated"))
    # Existing build outputs are task-owned and may only be replaced inside android/.build.
    for directory in (classes, dex, generated):
        if directory.exists():
            if WORK not in directory.resolve().parents:
                raise RuntimeError("Build directory escaped workspace")
            shutil.rmtree(directory)
        directory.mkdir()
    run("resources", [TOOLS / "aapt2.exe", "compile", "--dir", WORK / "res", "-o", WORK / "resources.zip"])
    run("link", [TOOLS / "aapt2.exe", "link", "-o", WORK / "resources.apk", "-I", staged_platform,
                 "--manifest", WORK / "AndroidManifest.xml", "--java", generated,
                 "--min-sdk-version", "26", "--target-sdk-version", "34", WORK / "resources.zip"])
    sources = sorted((WORK / "src").rglob("*.java")) + sorted(generated.rglob("*.java"))
    run("javac", [JAVAC, "-J-Duser.language=en", "-J-Duser.country=US", "-encoding", "UTF-8", "-source", "8", "-target", "8",
                  "-classpath", staged_platform, "-d", classes, *sources])
    with zipfile.ZipFile(WORK / "classes.jar", "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for compiled_class in sorted(classes.rglob("*.class")):
            archive.write(compiled_class, compiled_class.relative_to(classes).as_posix())
    LOG.append({"step": "jar-python", "exitCode": 0, "output": "Compiled classes archived using zipfile"})
    run("d8", [JAVA, "-cp", TOOLS / "lib/d8.jar", "com.android.tools.r8.D8", "--min-api", "26",
               "--lib", staged_platform, "--output", dex, WORK / "classes.jar"])
    unsigned = WORK / "unsigned.apk"
    shutil.copyfile(WORK / "resources.apk", unsigned)
    with zipfile.ZipFile(unsigned, "a", compression=zipfile.ZIP_DEFLATED) as archive:
        archive.write(dex / "classes.dex", "classes.dex")
    run("zipalign", [TOOLS / "zipalign.exe", "-P", "16", "-f", "4", unsigned, WORK / "aligned.apk"])
    password = KEYS / "password.txt"
    key = KEYS / "mail-reminder.p12"
    if not key.exists():
        if not password.exists():
            password.write_text(secrets.token_urlsafe(36), encoding="ascii")
        run("create-project-signing-key", [KEYTOOL, "-genkeypair", "-keystore", key,
             "-storetype", "PKCS12", "-alias", "mail-reminder", "-keyalg", "RSA", "-keysize", "3072",
             "-validity", "10000", "-dname", "CN=FEIAN Mail Reminder,O=Personal,C=CN",
             "-storepass:file", password, "-keypass:file", password, "-noprompt"])
    if not password.exists():
        raise RuntimeError("Signing password file is missing, preserve the existing key and restore its password")
    key_password = KEYS / "key-password.txt"
    if not key_password.exists():
        key_password.write_text(password.read_text(encoding="ascii").splitlines()[0] + "\n", encoding="ascii")
    signed = WORK / "signed.apk"
    run("sign", [JAVA, "-jar", TOOLS / "lib/apksigner.jar", "sign", "--ks", key, "--ks-key-alias", "mail-reminder",
                 "--ks-pass", "file:" + str(password), "--key-pass", "file:" + str(key_password),
                 "--v4-signing-enabled", "false", "--out", signed, WORK / "aligned.apk"])
    verification = run("verify-signature", [JAVA, "-jar", TOOLS / "lib/apksigner.jar", "verify", "--verbose", "--print-certs", signed])
    same_signer = None
    if previous_apk.is_file():
        previous_verification = run("verify-previous-signature", [JAVA, "-jar", TOOLS / "lib/apksigner.jar",
                                    "verify", "--verbose", "--print-certs", previous_apk])
        certificate_pattern = r"Signer #1 certificate SHA-256 digest: ([a-fA-F0-9]+)"
        current_certificate = re.search(certificate_pattern, verification)
        previous_certificate = re.search(certificate_pattern, previous_verification)
        same_signer = bool(current_certificate and previous_certificate and
                           current_certificate.group(1) == previous_certificate.group(1))
        if not same_signer:
            raise RuntimeError("APK signer differs from the preserved 1.3.0 release")
    run("verify-alignment", [TOOLS / "zipalign.exe", "-c", "-P", "16", "4", signed])
    badging = run("manifest-readback", [TOOLS / "aapt.exe", "dump", "badging", signed])
    if f"package: name='online.feian.mailreminder' versionCode='{VERSION_CODE}' versionName='{VERSION}'" not in badging:
        raise RuntimeError("APK package/version readback mismatch")
    if "targetSdkVersion:'34'" not in badging or "sdkVersion:'26'" not in badging:
        raise RuntimeError("SDK requirements mismatch")
    with zipfile.ZipFile(signed) as archive:
        if archive.testzip() is not None or not {"classes.dex", "AndroidManifest.xml", "resources.arsc"}.issubset(archive.namelist()):
            raise RuntimeError("APK ZIP integrity failure")
    host = WORK / "host-tests"
    host.mkdir(exist_ok=True)
    run("host-test-compile", [JAVAC, "-encoding", "UTF-8", "-d", host,
                              ROOT / "src/online/feian/mailreminder/SecurityPolicy.java", ROOT / "tests/SecurityPolicyTest.java"])
    run("host-tests", [JAVA, "-cp", host, "online.feian.mailreminder.SecurityPolicyTest"])
    run("concurrency-test-compile", [JAVAC, "-encoding", "UTF-8", "-d", host,
                                   ROOT / "src/online/feian/mailreminder/SecurityPolicy.java",
                                   ROOT / "src/online/feian/mailreminder/ReceiptQueue.java",
                                   ROOT / "src/online/feian/mailreminder/RelayWorkers.java",
                                   ROOT / "tests/AndroidConcurrencyTest.java"])
    run("concurrency-tests", [JAVA, "-cp", host, "online.feian.mailreminder.AndroidConcurrencyTest"])
    if previous_hash is not None and hashlib.sha256(previous_apk.read_bytes()).hexdigest() != previous_hash:
        raise RuntimeError("The preserved 1.3.0 APK changed during this build")
    shutil.copyfile(signed, OUTPUT)
    result = {"status": "pass", "apk": str(OUTPUT), "size": OUTPUT.stat().st_size,
              "sha256": hashlib.sha256(OUTPUT.read_bytes()).hexdigest(), "package": "online.feian.mailreminder",
              "version": VERSION, "versionCode": VERSION_CODE, "minSdk": 26, "targetSdk": 34,
              "previousReleaseSha256": previous_hash, "sameSignerAs130": same_signer,
              "validation": "aapt manifest, signature, alignment, ZIP integrity, host tests",
              "deviceAcceptance": "not run", "steps": LOG}
    (ROOT / f"build-result-{VERSION}.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print("APK pass " + str(OUTPUT))
    if WORK.parent.resolve() == Path(tempfile.gettempdir()).resolve() and WORK.name.startswith("feian-mail-android-"):
        shutil.rmtree(WORK)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        EVIDENCE.mkdir(exist_ok=True)
        (ROOT / f"build-failure-{VERSION}.json").write_text(json.dumps({"status": "fail", "error": str(error), "steps": LOG}, ensure_ascii=False, indent=2), encoding="utf-8")
        with (ROOT / f"build-failures-{VERSION}.jsonl").open("a", encoding="utf-8") as evidence:
            evidence.write(json.dumps({"status": "fail", "error": str(error), "steps": LOG}, ensure_ascii=False) + "\n")
        raise
