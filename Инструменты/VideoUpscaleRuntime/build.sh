#!/usr/bin/env bash
set -euo pipefail
test "${MSYSTEM:-}" = UCRT64
root=$(realpath "${1:?workspace root}")
capture="${3:-$root/Runtime/Capture/Minimal-8.1}"
build="$root/Runtime/VideoUpscale/Build-8.1-upscale-5"
codecs=$(realpath "${2:?directory containing the pinned source archives}")
prefix="$build/prefix"
mkdir -p "$build/work" "$build/logs" "$prefix"
recipe=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
bash "$recipe/build-codecs.sh" "$codecs" "$build"
export PKG_CONFIG_PATH="$build/codecs/lib/pkgconfig:$capture/prefix/lib/pkgconfig"
export PKG_CONFIG_LIBDIR="$PKG_CONFIG_PATH"
cd "$build/work"
"$capture/work/ffmpeg/configure" --prefix="$prefix" --pkg-config-flags=--static \
 --disable-autodetect --disable-everything --disable-network --enable-shared \
 --disable-static --disable-debug --disable-doc --disable-ffplay --enable-zlib \
 --enable-w32threads --disable-pthreads --enable-mediafoundation --enable-d3d11va \
 --enable-libvpx --enable-libopus --enable-libopenh264 --enable-ffnvcodec --enable-nvenc \
 --enable-libaom --enable-libsvtav1 --enable-libkvazaar \
 --disable-cuvid --disable-cuda-llvm \
 --enable-encoder=ffv1,h264_mf,h264_nvenc,hevc_nvenc,av1_nvenc,libopenh264,libaom_av1,libsvtav1,libkvazaar,libvpx_vp9,aac,libopus,flac,rawvideo,pcm_s16le,png,movtext,srt,webvtt \
 --enable-decoder=ffv1,wrapped_avframe,rawvideo,h264,hevc,vp8,vp9,libaom_av1,mpeg4,mpeg2video,aac,ac3,eac3,dca,opus,mp3,flac,vorbis,pcm_u8,pcm_s16le,pcm_s24le,pcm_s32le,pcm_f32le,png,mjpeg,bmp,gif,webp,movtext,subrip,webvtt \
 --enable-parser=h264,hevc,vp8,vp9,av1,mpeg4video,mpegaudio,aac,ac3,dca,opus,flac,png,mjpeg \
 --enable-bsf=av1_metadata \
 --enable-demuxer=concat,rawvideo,pcm_s16le,mov,matroska,avi,wav,mp3,flac,ogg,aac,image2,png_pipe,jpeg_pipe,bmp_pipe,gif,webp_pipe \
 --enable-muxer=mp4,matroska,webm,rawvideo,pcm_s16le,wav,image2,null,framehash \
 --enable-protocol=file,pipe,fd --enable-indev=lavfi \
 --enable-filter=anullsrc,sine,aresample,aformat,apad,atrim,anull,amix,volume,asetpts,format,null,scale,setsar,setpts,trim,fps,pad,color,overlay,crop,select,showinfo,tpad,testsrc2,blend,unsharp,atadenoise,transpose,hflip,vflip,rotate \
 --extra-cflags="-I$capture/prefix/include -I$build/codecs/include -DKVZ_STATIC_LIB" \
 --extra-ldflags="-L$capture/prefix/lib -L$build/codecs/lib -static-libgcc -static-libstdc++ -static" \
 --extra-version=lopata-video-upscale-5 >"$build/logs/configure.log" 2>&1
make -j8 >"$build/logs/build.log" 2>&1
make install >>"$build/logs/build.log" 2>&1
