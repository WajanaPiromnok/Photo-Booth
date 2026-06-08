cmake_minimum_required(VERSION 3.16)

project(obsbot-control LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 11)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

if (NOT DEFINED OBSBOT_SDK_PATH)
    set(OBSBOT_SDK_PATH "$ENV{OBSBOT_SDK_PATH}")
endif ()

if ("${OBSBOT_SDK_PATH}" STREQUAL "")
    message(FATAL_ERROR "Set OBSBOT_SDK_PATH to the libdev SDK root.")
endif ()

add_executable(obsbot-control src/obsbot_control.cpp)
target_include_directories(obsbot-control PRIVATE "${OBSBOT_SDK_PATH}/include")

if (APPLE)
    if (CMAKE_OSX_ARCHITECTURES STREQUAL "arm64")
        set(OBSBOT_SDK_LIB_DIR "${OBSBOT_SDK_PATH}/macos/arm64-release")
    else ()
        set(OBSBOT_SDK_LIB_DIR "${OBSBOT_SDK_PATH}/macos/x86_64-release")
    endif ()

    target_link_directories(obsbot-control PRIVATE "${OBSBOT_SDK_LIB_DIR}")
    target_link_libraries(obsbot-control PRIVATE dev)
    set_target_properties(obsbot-control PROPERTIES
        BUILD_RPATH "@executable_path"
        INSTALL_RPATH "@executable_path")
else ()
    if (CMAKE_BUILD_TYPE STREQUAL "Debug")
        set(OBSBOT_SDK_LIB_DIR "${OBSBOT_SDK_PATH}/windows/win64-debug")
    else ()
        set(OBSBOT_SDK_LIB_DIR "${OBSBOT_SDK_PATH}/windows/win64-release")
    endif ()

    target_compile_definitions(obsbot-control PRIVATE UNICODE _UNICODE _CRT_SECURE_NO_WARNINGS _CRT_NONSTDC_NO_WARNINGS)
    target_link_directories(obsbot-control PRIVATE "${OBSBOT_SDK_LIB_DIR}")
    target_link_libraries(obsbot-control PRIVATE libdev)
endif ()
