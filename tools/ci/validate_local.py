#!/usr/bin/env python3
"""Owner-run suites only; never dispatches CI or signs a production app."""
import argparse
import os
from pathlib import Path
import platform
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
XCODE = "/Applications/Xcode_26.6.app/Contents/Developer"


def run(command, cwd=ROOT, env=None):
    print(f"\n[{cwd}] {' '.join(command)}", flush=True)
    subprocess.run(command, cwd=cwd, env=env, check=True)


def validate(suite):
    env = {**os.environ, "DEVELOPER_DIR": XCODE}
    if suite in ("all", "native", "benchmarks"):
        if platform.system() != "Darwin" or not Path(XCODE).is_dir():
            raise RuntimeError("Install Xcode 26.6 on macOS before running native checks.")
    if suite in ("all", "native"):
        run(["python3", "tools/demo/recording/test_cursor_camera.py"], env=env)
        run(["make", "test"], env=env)
        run(["swift", "test", "--package-path", "apps/macos", "-c", "release"], env=env)
        run(["make", "app"], env=env)
        for configuration in ("Debug", "Release"):
            run(["xcodebuild", "-quiet", "-project", "apps/macos/Annotator.xcodeproj",
                 "-scheme", "Annotator", "-configuration", configuration,
                 "-destination", "platform=macOS",
                 "-derivedDataPath", f"build/LocalValidation-{configuration}",
                 "CODE_SIGN_IDENTITY=-", "CODE_SIGNING_REQUIRED=NO", "build", "test"], env=env)
    if suite in ("all", "benchmarks"):
        run(["bash", "tools/macos/benchmark.sh", "--samples", "21", "--warmup", "3",
             "--output", "build/performance/local-validation.json"], env=env)
    if suite in ("all", "browser"):
        website = ROOT / "website"
        version = subprocess.check_output(["node", "--version"], cwd=website, text=True).strip()
        if version != "v26.8.1":
            raise RuntimeError(f"Use Node 26.8.1 for browser validation; found {version}.")
        # Clear caller overrides so root/closed means the same thing as in CI.
        browser_env = {**os.environ, "SITE_URL": "", "DISTRIBUTION_METADATA_FILE": "",
                       "WINDOWS_LISTING_FILE": ""}
        for script in ("typecheck", "build", "test", "test:e2e",
                       "test:distribution", "test:pages", "test:windows"):
            run(["npm", "run", script], cwd=website, env=browser_env)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("suite", choices=("all", "native", "benchmarks", "browser"))
    args = parser.parse_args()
    try:
        validate(args.suite)
    except (OSError, RuntimeError, subprocess.CalledProcessError) as error:
        print(f"Local validation failed: {error}", file=sys.stderr)
        return error.returncode if isinstance(error, subprocess.CalledProcessError) and error.returncode > 0 else 1
    print("Requested local suite passed. No release artifact was certified or uploaded.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
