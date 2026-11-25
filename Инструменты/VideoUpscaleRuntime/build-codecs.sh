#!/usr/bin/env bash
set -euo pipefail
archives=$1
build=$2
mkdir -p "$build/codecs-src"
prefix=$(cygpath -m "$build/codecs")
cd "$build/codecs-src"

extract() {
    local name=$1 archive=$2 sha=$3 strip=${4:-1}
    printf '%s  %s\n' "$sha" "$archives/$archive" | sha256sum --check --status
    if [[ ! -f "$name/.extracted" ]]; then
        mkdir -p "$name"
        tar -xzf "$archives/$archive" -C "$name" --strip-components="$strip"
        touch "$name/.extracted"
    fi
}
install_codec() {
    local name=$1
    shift
    cmake -S "$name" -B "build-$name" -G Ninja -DCMAKE_BUILD_TYPE=Release \
        -DCMAKE_INSTALL_PREFIX="$prefix" -DBUILD_SHARED_LIBS=OFF "$@" >"$build/logs/$name.log" 2>&1
    cmake --build "build-$name" --target install -j 8 >>"$build/logs/$name.log" 2>&1
}

extract aom aom-v3.12.1.tar.gz 1453a5e3e83247b4eea865c96a4d7d3334ae2fe4fd159e140f9de20258be86e5 0
extract svt svt-av1-v3.0.2.tar.gz 5af7f4376aa00a4dee32df04be1cdd1983c9940bcc019ee6b29bb8a216bae2f8
extract kvazaar kvazaar-v2.3.2.tar.gz ddd0038696631ca5368d8e40efee36d2bbb805854b9b1dda8b12ea9b397ea951
cpu_archive="$archives/cpuinfo-e649baa.zip"
printf '%s  %s\n' d2cfc3b4120ad2f17a7c6cc43d0547ecfb9f375b7918d723d234100822aa490e "$cpu_archive" | sha256sum --check --status
if [[ ! -f cpuinfo/.extracted ]]; then
    mkdir -p cpuinfo
    /c/Windows/System32/tar.exe -xf "$(cygpath -m "$cpu_archive")" -C "$(cygpath -m "$PWD/cpuinfo")" --strip-components=1
    touch cpuinfo/.extracted
fi

# NASM 3 moved the documentation for -Ox from -hf to -h -O.
if ! grep -q nasm_optimization aom/build/cmake/aom_optimization.cmake; then
    sed -i -e '/OUTPUT_VARIABLE nasm_helptext)/a\  execute_process(COMMAND ${CMAKE_ASM_NASM_COMPILER} -h -O OUTPUT_VARIABLE nasm_optimization)' \
        -e 's/${nasm_helptext}" MATCHES "-Ox/${nasm_helptext}${nasm_optimization}" MATCHES "-Ox/' \
        aom/build/cmake/aom_optimization.cmake
fi
install_codec aom -DENABLE_NASM=ON -DENABLE_DOCS=0 -DENABLE_EXAMPLES=0 -DENABLE_TOOLS=0 -DENABLE_TESTS=0 -DCONFIG_AV1_DECODER=1
install_codec svt -DBUILD_APPS=OFF -DBUILD_TESTING=OFF -DUSE_CPUINFO=LOCAL \
    -DFETCHCONTENT_SOURCE_DIR_CPUINFO="$(cygpath -m "$PWD/cpuinfo")" -DFETCHCONTENT_FULLY_DISCONNECTED=ON \
    -DEXCLUDE_HASH=ON -DSVT_AV1_LTO=OFF -DENABLE_AVX512=OFF
install_codec kvazaar -DBUILD_TESTS=OFF -DBUILD_KVAZAAR_BINARY=OFF -DGIT_SUBMODULE=OFF
