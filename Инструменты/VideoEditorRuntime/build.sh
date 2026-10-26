#!/usr/bin/env bash
set -euo pipefail
test "${MSYSTEM:-}" = UCRT64
root=$(realpath "${1:?workspace root}")
capture="$root/Runtime/Capture/Minimal-8.1"
build="$root/Runtime/VideoEditor/Build-8.1"
prefix="$build/prefix"
mkdir -p "$build/work" "$build/logs" "$prefix"
export PKG_CONFIG_PATH="$capture/prefix/lib/pkgconfig"
export PKG_CONFIG_LIBDIR="$PKG_CONFIG_PATH"
cd "$build/work"
"$capture/work/ffmpeg/configure" --prefix="$prefix" --pkg-config-flags=--static \
 --disable-autodetect --disable-everything --disable-network --enable-shared \
 --disable-static --disable-debug --disable-doc --disable-ffplay --enable-zlib \
 --enable-w32threads --disable-pthreads --enable-mediafoundation --enable-d3d11va \
 --enable-libvpx --enable-libopus --enable-libopenh264 --enable-ffnvcodec --enable-nvenc \
 --disable-cuvid --disable-cuda-llvm \
 --enable-encoder=h264_mf,h264_nvenc,libopenh264,libvpx_vp9,aac,libopus,rawvideo,pcm_s16le,pcm_f32le,png,subrip,movtext,webvtt \
 --enable-decoder=wrapped_avframe,rawvideo,h264,hevc,vp8,vp9,av1,mpeg4,mpeg2video,aac,opus,mp3,flac,vorbis,pcm_u8,pcm_s16le,pcm_s24le,pcm_s32le,pcm_f32le,png,mjpeg,bmp,gif,webp,subrip,movtext,webvtt,ass \
 --enable-parser=h264,hevc,vp8,vp9,av1,mpeg4video,mpegaudio,aac,opus,flac,png,mjpeg \
 --enable-demuxer=rawvideo,pcm_s16le,pcm_f32le,mov,matroska,avi,wav,mp3,flac,ogg,aac,image2,png_pipe,jpeg_pipe,bmp_pipe,gif,webp_pipe,srt,webvtt,ass \
 --enable-muxer=mp4,matroska,webm,rawvideo,pcm_s16le,pcm_f32le,wav,image2,srt,webvtt \
 --enable-protocol=file,pipe --enable-indev=lavfi \
 --enable-filter=anullsrc,aresample,aformat,apad,atrim,anull,amix,alimiter,volume,afade,adelay,atempo,asetpts,format,null,scale,setsar,setpts,trim,fps,pad,color,overlay,crop,rotate,lutrgb,hue,fade,select,showinfo,scale2ref,tpad \
 --extra-cflags="-I$capture/prefix/include" \
 --extra-ldflags="-L$capture/prefix/lib -static-libgcc -static-libstdc++ -static" \
 --extra-version=lopata-video-editor-1 >"$build/logs/configure.log" 2>&1
make -j8 >"$build/logs/build.log" 2>&1
make install >>"$build/logs/build.log" 2>&1
