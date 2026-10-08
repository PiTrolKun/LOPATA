#!/usr/bin/env bash
set -euo pipefail
test "${MSYSTEM:-}" = UCRT64
root=$(realpath "${1:?Build directory required}")
prefix="$root/prefix"
jobs=${2:-4}
mkdir -p "$prefix" "$root/work" "$root/logs"
export PKG_CONFIG_PATH="$prefix/lib/pkgconfig"
export PKG_CONFIG_LIBDIR="$prefix/lib/pkgconfig"
for component in ffmpeg opus lame; do
    if ! test -d "$root/work/$component"; then
        mkdir "$root/work/$component"
        tar -xzf "$root/sources/$component.tar.gz" --strip-components=1 -C "$root/work/$component"
    fi
done
if grep -q "char prev = ' ';" "$root/work/ffmpeg/libavformat/ffmetadec.c"; then
    (cd "$root/work/ffmpeg" && patch -p1 < "$root/ffmetadata-escape.patch")
fi
if ! test -f "$prefix/lib/libopus.a"; then
    (
        cmake -S "$root/work/opus" -B "$root/work/opus-build" -G Ninja \
            -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX="$(cygpath -m "$prefix")" \
            -DBUILD_SHARED_LIBS=OFF -DOPUS_BUILD_PROGRAMS=OFF -DOPUS_BUILD_TESTING=OFF \
            -DOPUS_INSTALL_PKG_CONFIG_MODULE=ON
        cmake --build "$root/work/opus-build" --parallel "$jobs"
        cmake --install "$root/work/opus-build"
    ) >"$root/logs/opus.log" 2>&1
fi
if ! test -f "$prefix/lib/libmp3lame.a"; then
    (
        cd "$root/work/lame"
        ./configure --prefix="$prefix" --disable-shared --enable-static \
            --disable-frontend --disable-decoder --disable-asm
        make -j"$jobs"
        make install
    ) >"$root/logs/lame.log" 2>&1
fi
mkdir -p "$root/work/ffmpeg-build"
(
    cd "$root/work/ffmpeg-build"
    ../ffmpeg/configure --prefix="$prefix" --pkg-config-flags=--static \
        --disable-autodetect --disable-everything --disable-network \
        --enable-shared --disable-static --disable-debug --disable-doc --disable-ffplay \
        --disable-avdevice --disable-swscale --enable-ffmpeg --enable-ffprobe \
        --enable-w32threads --disable-pthreads --enable-libopus --enable-libmp3lame \
        --enable-encoder=libopus,libmp3lame,flac,pcm_s16le \
        --enable-decoder=opus,mp3,mp3float,flac,pcm_s16le \
        --enable-parser=opus,mpegaudio,flac \
        --enable-demuxer=ogg,mp3,flac,wav,ffmetadata \
        --enable-muxer=opus,mp3,flac,wav,ffmetadata,null \
        --enable-protocol=file,pipe --enable-filter=aresample,aformat,anull \
        --extra-cflags="-I$prefix/include" \
        --extra-ldflags="-L$prefix/lib -static-libgcc -static-libstdc++ -static" \
        --extra-version=lopata-audio-1
    make -j"$jobs"
    make install
) >"$root/logs/ffmpeg.log" 2>&1
pacman -Q >"$root/toolchain-packages.txt"
gcc --version >"$root/compiler.txt"
echo "Built isolated music audio runtime in $prefix/bin"
