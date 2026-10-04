"""Check the actual APK, so delivery cannot silently switch back to a debug build."""
import argparse
import subprocess
import zipfile


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("apk")
    parser.add_argument("--aapt", required=True)
    args = parser.parse_args()
    badging = subprocess.check_output(
        [args.aapt, "dump", "badging", args.apk], text=True, encoding="utf-8"
    )
    if "package: name='com.liuzhuan.app'" not in badging:
        raise SystemExit("FAIL: unexpected Android package")
    if "application-debuggable" in badging.splitlines():
        raise SystemExit("FAIL: distribution APK is debuggable")
    manifest = subprocess.check_output(
        [args.aapt, "dump", "xmltree", args.apk, "AndroidManifest.xml"],
        text=True, encoding="utf-8"
    )
    if "com.liuzhuan.app.ACTION_CLIPHOOK" not in manifest:
        raise SystemExit("FAIL: clipboard receiver action is missing")
    with zipfile.ZipFile(args.apk) as apk:
        hook = apk.read("assets/xposed_init").decode("utf-8").strip()
        if hook != "com.liuzhuan.app.xposed.ClipHook":
            raise SystemExit("FAIL: unexpected Xposed entry point")
    print("PASS: non-debuggable APK, package, clipboard receiver, and Xposed entry point")


if __name__ == "__main__":
    main()
