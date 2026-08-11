cmake_minimum_required(VERSION 3.15)
project(overtranslate_bergamot LANGUAGES CXX)

set(BERGAMOT_SOURCE_DIR "" CACHE PATH "Path to a browsermt/bergamot-translator checkout")
if(NOT EXISTS "${BERGAMOT_SOURCE_DIR}/CMakeLists.txt")
  message(FATAL_ERROR "BERGAMOT_SOURCE_DIR must point to a Bergamot source checkout")
endif()

add_subdirectory("${BERGAMOT_SOURCE_DIR}" bergamot)

add_library(overtranslate_bergamot SHARED overtranslate_bergamot.cpp)
target_include_directories(overtranslate_bergamot PRIVATE "${BERGAMOT_SOURCE_DIR}/src")
target_link_libraries(overtranslate_bergamot PRIVATE bergamot-translator)
target_compile_features(overtranslate_bergamot PRIVATE cxx_std_17)
