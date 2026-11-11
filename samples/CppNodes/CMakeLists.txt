# Builds the sample C++ node library with CMake:
#
#     cmake -S . -B build -A x64
#     cmake --build build --config Release
#
# and SampleCppNodes.dll is in build/Release.

cmake_minimum_required(VERSION 3.15)
project(SampleCppNodes CXX)

set(CMAKE_CXX_STANDARD 14)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Where sirius_node.hpp is: the cpp folder of the SDK this sample came in,
# the application's own cpp folder, or wherever the two headers were copied.
if(NOT SIRIUS_INCLUDE)
    foreach(candidate "${CMAKE_CURRENT_SOURCE_DIR}/../cpp" "${CMAKE_CURRENT_SOURCE_DIR}/../../src/Sirius.Vision/Cpp")
        if(EXISTS "${candidate}/sirius_node.hpp")
            set(SIRIUS_INCLUDE "${candidate}")
            break()
        endif()
    endforeach()
endif()

if(NOT EXISTS "${SIRIUS_INCLUDE}/sirius_node.hpp")
    message(FATAL_ERROR "sirius_node.hpp was not found. Configure with -DSIRIUS_INCLUDE=<its folder>.")
endif()

add_library(SampleCppNodes SHARED sample_nodes.cpp)
target_include_directories(SampleCppNodes PRIVATE "${SIRIUS_INCLUDE}")

if(MSVC)
    target_compile_options(SampleCppNodes PRIVATE /W4)
endif()

# A library built on OpenCV adds:
#
#     find_package(OpenCV REQUIRED)
#     target_link_libraries(SampleCppNodes PRIVATE ${OpenCV_LIBS})
#
# and then needs OpenCV's DLLs beside its own, or on PATH.
