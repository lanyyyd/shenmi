cmake_minimum_required(VERSION 3.20)
project(steameya_island CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_executable(steameya-island WIN32 island.cpp)
target_compile_definitions(steameya-island PRIVATE
    UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN _WIN32_WINNT=0x0A00 WINVER=0x0A00)
target_link_libraries(steameya-island PRIVATE
    d2d1 dwrite windowscodecs ole32 oleaut32 shell32 shcore user32 gdi32 dwmapi)
