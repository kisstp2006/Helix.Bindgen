cmake_minimum_required(VERSION 3.28)
project(Helix.Bindgen LANGUAGES CXX)

if(NOT COMMAND helix_add_library)
    include("${CMAKE_CURRENT_SOURCE_DIR}/External/Helix.CMake/Helix.cmake")
endif()

helix_require(Core)
helix_require(Platform)
helix_require(Scripting)
include("${CMAKE_CURRENT_SOURCE_DIR}/CMake/HelixBindgen.cmake")

if(HELIX_BUILD_TESTS AND PROJECT_IS_TOP_LEVEL)
    # The sample module: tagged headers, their generated C++ in an object library that the C++ tests and (on the
    # desktop) a shared library for the C# tests both take.
    set(sample_dir "${CMAKE_CURRENT_SOURCE_DIR}/Tests/Sample")
    add_library(helix_bindgen_sample OBJECT "${sample_dir}/Sample.cpp" "${sample_dir}/Sample.h" "${sample_dir}/SampleEvents.h")
    target_include_directories(helix_bindgen_sample PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/Tests")
    target_link_libraries(helix_bindgen_sample PUBLIC Helix::Scripting)
    helix_apply_options(helix_bindgen_sample)
    helix_bindgen(helix_bindgen_sample MODULE Sample NAMESPACE Sample HEADERS "${sample_dir}/Sample.h"
                  INCLUDE_ROOTS "${CMAKE_CURRENT_SOURCE_DIR}/Tests" CS_OUTPUT sample_cs)

    helix_add_test_suite(helix_bindgen_tests EXCLUDE "/Sample/" DEPS helix_bindgen_sample Helix::Scripting)

    # Both directions on the desktop (not under AddressSanitizer, whose runtime the .NET host does not load first):
    # the scripts assembly (the sample's C# bindings and C# classes) is built, C++ proxies of its classes are generated
    # from it into the shared library, and the test program (with the proxies' C# exports) loads that library, calls
    # it through the bindings and runs its C++ checks of the proxies.
    if((HELIX_PLATFORM STREQUAL "windows" OR HELIX_PLATFORM STREQUAL "linux") AND NOT HELIX_ENABLE_ASAN)
        set(managed_dir "${CMAKE_CURRENT_SOURCE_DIR}/Tests/Managed")
        set(managed_artifacts "${CMAKE_CURRENT_BINARY_DIR}/Managed")
        set(scripts_dll "${managed_artifacts}/bin/HelixBindgen.Scripts/release/HelixBindgen.Scripts.dll")
        set(runner_dll "${managed_artifacts}/bin/HelixBindgen.Tests/release/HelixBindgen.Tests.dll")
        file(GLOB_RECURSE scripts_sources CONFIGURE_DEPENDS "${managed_dir}/Scripts/*.cs" "${managed_dir}/Scripts/*.csproj")
        file(GLOB_RECURSE runner_sources CONFIGURE_DEPENDS "${managed_dir}/Runner/*.cs" "${managed_dir}/Runner/*.csproj")
        list(FILTER scripts_sources EXCLUDE REGEX "/(bin|obj)/")
        list(FILTER runner_sources EXCLUDE REGEX "/(bin|obj)/")
        add_custom_command(
            OUTPUT "${scripts_dll}"
            COMMAND "${HELIX_DOTNET}" build "${managed_dir}/Scripts/HelixBindgen.Scripts.csproj" -c Release
                    --artifacts-path "${managed_artifacts}" --nologo -v quiet "-p:HelixBindingsFile=${sample_cs}"
                    "-p:HelixInteropFile=${HELIX_BINDGEN_RUNTIME}"
            DEPENDS ${scripts_sources} "${sample_cs}" "${HELIX_BINDGEN_RUNTIME}"
            COMMENT "Building the C# scripts of the tests")

        add_library(helix_bindgen_sample_shared SHARED $<TARGET_OBJECTS:helix_bindgen_sample> "${sample_dir}/ProxyChecks.cpp")
        target_include_directories(helix_bindgen_sample_shared PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/Tests")
        target_link_libraries(helix_bindgen_sample_shared PRIVATE Helix::Scripting)
        helix_apply_options(helix_bindgen_sample_shared)
        helix_bindgen(helix_bindgen_sample_shared MODULE HelixBindgen.Scripts ASSEMBLY "${scripts_dll}" PROXIES Scripts.Counter Scripts.IScorer
                      NAMESPACE Sample REFERENCES "${sample_dir}/Sample.h" INCLUDE_ROOTS "${CMAKE_CURRENT_SOURCE_DIR}/Tests" CS_OUTPUT exports_cs)

        add_custom_command(
            OUTPUT "${runner_dll}"
            COMMAND "${HELIX_DOTNET}" build "${managed_dir}/Runner/HelixBindgen.Tests.csproj" -c Release
                    --artifacts-path "${managed_artifacts}" --nologo -v quiet "-p:HelixExportsFile=${exports_cs}"
                    "-p:HelixScriptsAssembly=${scripts_dll}"
            DEPENDS ${runner_sources} "${exports_cs}" "${scripts_dll}"
            COMMENT "Building the C# test program of the bindings")
        add_custom_target(helix_bindgen_managed_tests ALL DEPENDS "${runner_dll}")
        add_dependencies(helix_bindgen_managed_tests helix_bindgen_sample_shared)
        add_test(NAME helix_bindgen_managed COMMAND "${HELIX_DOTNET}" "${runner_dll}" "$<TARGET_FILE:helix_bindgen_sample_shared>")
    endif()
endif()
