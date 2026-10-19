# DTS helper for AtmosBridge, built from the DTSX_decoder submodule:
#   cmake -S dtsx-objects -B dtsx-objects/build && cmake --build dtsx-objects/build --config Release
cmake_minimum_required(VERSION 3.20)
project(dtsx-objects C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_MSVC_RUNTIME_LIBRARY MultiThreaded) # static CRT: nothing to install next to the bridge
set(DTSX ${CMAKE_CURRENT_SOURCE_DIR}/DTSX_decoder)

# Sources of DTSX_decoder's build.bat, minus its CLI (main.cpp), pipeline.cpp (included by main.cpp here),
# the DTS-UHD path (p2_decoder.cpp embeds DTS's DTSXDecoder.dll) and the --help QR codes (support_qr.cpp)
foreach(f bitstream core_decoder dca_context dmix_tables exss_parser idct_fixed idct_float interpolator
          interpolator_fixed interpolator_float lbr_decoder ta xll_decoder)
    list(APPEND dcadec ${DTSX}/third_party/dcadec/${f}.c)
endforeach()
file(GLOB_RECURSE src ${DTSX}/src/*.cpp)
list(FILTER src EXCLUDE REGEX "/(main|pipeline|p2_decoder|p2_decoder_unsupported|support_qr)\\.cpp$")

add_executable(dtsx-objects main.cpp ${src} ${dcadec})
target_include_directories(dtsx-objects PRIVATE ${DTSX} ${DTSX}/src ${DTSX}/third_party/dcadec)
target_compile_definitions(dtsx-objects PRIVATE _USE_MATH_DEFINES _CRT_SECURE_NO_WARNINGS NDEBUG)
if(MSVC)
    target_compile_options(dtsx-objects PRIVATE /utf-8 /wd4146 $<$<COMPILE_LANGUAGE:C>:/W0>)
endif()
