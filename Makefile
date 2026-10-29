DEVELOPER_DIR ?= /Applications/Xcode.app/Contents/Developer
export DEVELOPER_DIR

.PHONY: test build conformance conformance-check conformance-check-data generate app signed-dev native-test signed-native-test clean-check ai-skill release-check release-tests
test:
	swift test --package-path apps/macos

build:
	swift build --package-path apps/macos

# Writes reference/conformance from the Mac sources. Commit source changes first.
conformance:
	swift run --package-path apps/macos ConformanceExport

conformance-check:
	swift run --package-path apps/macos ConformanceExport --check

# Everything but the rendered images, which depend on the macOS version and GPU (used by CI).
conformance-check-data:
	swift run --package-path apps/macos ConformanceExport --check-data

generate:
	xcodegen generate --spec apps/macos/project.yml --project apps/macos

app: generate
	xcodebuild -project apps/macos/Annotator.xcodeproj -scheme Annotator -configuration Debug -derivedDataPath build/UnsignedDerivedData CODE_SIGN_IDENTITY=- CODE_SIGNING_REQUIRED=NO build

signed-dev: generate
	bash tools/macos/build-signed-dev.sh
	codesign --verify --deep --strict "build/DerivedData/Build/Products/Debug/Annotator Dev.app"

native-test: generate
	xcodebuild -project apps/macos/Annotator.xcodeproj -scheme Annotator -configuration Debug -destination 'platform=macOS,arch=arm64' -derivedDataPath build/UnsignedDerivedData CODE_SIGN_IDENTITY=- CODE_SIGNING_REQUIRED=NO test

signed-native-test: generate
	bash tools/macos/build-signed-dev.sh test

clean-check:
	git diff --check

ai-skill:
	mkdir -p website/public/downloads
	python3 -m zipfile -c website/public/downloads/annotator-skill.zip integrations/computer-use/annotator

release-check:
	python3 tools/release/release.py check --candidate
	python3 tools/release/test_release.py

release-tests:
	python3 -m unittest discover -s tools/release -p 'test_*release.py'
	python3 -m unittest discover -s tools/release -p 'test_distribution.py'
	python3 -m unittest discover -s tools/release -p 'test_package_app.py'
	python3 -m unittest discover -s tools/release -p 'test_publish_website.py'
	python3 tools/windows/test_publish_windows.py
