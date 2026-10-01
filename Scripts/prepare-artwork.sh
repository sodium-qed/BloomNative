#!/bin/zsh
# Artwork is fetched from its creator for the user's local build, not redistributed.
set -euo pipefail
cd "${0:A:h}/.."
mkdir -p Resources
if [[ ! -f Resources/BloomOriginal.mp4 ]]; then
  curl --fail --location --output Resources/BloomOriginal.mp4.part 'https://sixnfive.com/wp-content/uploads/2021/07/curls_hero_05_anim_19_light2.mp4'
  mv Resources/BloomOriginal.mp4.part Resources/BloomOriginal.mp4
fi
actual_hash=$(shasum -a 256 Resources/BloomOriginal.mp4 | cut -d ' ' -f 1)
if [[ "$actual_hash" != '01e08e7efd67574db59352a3cb8be79aeb8e65120bb8aba2f27047e501d5bb75' ]]; then
  print -u2 'The source artwork has changed. Review its provenance before using it.'
  exit 1
fi
if [[ ! -f Resources/BloomPoster.jpg ]]; then
  swift Scripts/extract-poster.swift "$PWD/Resources/BloomOriginal.mp4" "$PWD/Resources/BloomPoster.jpg"
fi
