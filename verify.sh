#!/bin/zsh
set -euo pipefail
cd "${0:A:h}"
swiftc -swift-version 5 Sources/BloomLanguage.swift Sources/LidSensor.swift Sources/BloomFrames.swift Sources/BloomRenderer.swift Tests/main.swift -o Tests/verify -framework AppKit -framework MetalKit -framework ScreenSaver -framework IOKit
Tests/verify "$PWD"
swiftc Sources/BloomLanguage.swift Tests/language.swift -o Tests/language-verify
Tests/language-verify
