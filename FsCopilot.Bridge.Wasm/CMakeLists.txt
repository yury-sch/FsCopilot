cmake_minimum_required(VERSION 4.1)
project(FsCopilot_Bridge_Wasm)

set(CMAKE_CXX_STANDARD 14)

include_directories(.)
include_directories(Interpolation)
include_directories(Protocol)

add_executable(FsCopilot_Bridge_Wasm
        Interpolation/Interpolators.h
        Interpolation/StreamBuffer.h
        Interpolation/TimeSync.h
        Protocol/FsCopilotProtocol.h
        FsCopilotWasm.cpp
        FsCopilotWasm.h)
