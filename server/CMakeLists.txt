cmake_minimum_required(VERSION 3.20)
project(DuckovCoopServer LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

if (MSVC)
    add_compile_options(/W4 /permissive- /utf-8 /Zc:__cplusplus)
    add_compile_definitions(_CRT_SECURE_NO_WARNINGS NOMINMAX WIN32_LEAN_AND_MEAN)
endif()

add_library(coop_core STATIC
    third_party/tinyxml2/tinyxml2.cpp
    src/core/Log.cpp
    src/core/Json.cpp
    src/content/XmlDoc.cpp
    src/content/ContentHash.cpp
    src/content/ContentStore.cpp
    src/combat/DamageFormula.cpp
    src/combat/StatModel.cpp
    src/combat/BuffModel.cpp
    src/protocol/Wire.cpp
    src/protocol/Messages.cpp
    src/net/UdpSocket.cpp
    src/net/ReliableChannel.cpp
    src/gate/Gate.cpp
    src/world/World.cpp
    src/world/ItemLedger.cpp
    src/world/ItemRegistry.cpp
    src/storage/FileStorage.cpp
    src/storage/InMemoryStorage.cpp
    src/storage/InMemoryRaidStore.cpp
    src/storage/InMemorySettlementQueue.cpp
    src/storage/StorageFactory.cpp
)

# third-party sources are compiled without our warning level
if (MSVC)
    set_source_files_properties(third_party/tinyxml2/tinyxml2.cpp PROPERTIES COMPILE_FLAGS "/W0")
endif()

target_include_directories(coop_core PUBLIC src third_party/tinyxml2)

if (WIN32)
    target_link_libraries(coop_core PUBLIC ws2_32)
else()
    # POSIX: std::thread needs pthread; SHA-256 is vendored so no bcrypt/OpenSSL.
    find_package(Threads REQUIRED)
    target_link_libraries(coop_core PUBLIC Threads::Threads)
endif()

add_executable(server src/main.cpp)
target_link_libraries(server PRIVATE coop_core)

add_executable(golden_gen tools/golden_gen.cpp)
target_link_libraries(golden_gen PRIVATE coop_core)

add_executable(json_to_xml tools/json_to_xml.cpp)
target_link_libraries(json_to_xml PRIVATE coop_core)

enable_testing()
add_executable(coop_tests
    tests/TestHarness.cpp
    tests/tests_protocol.cpp
    tests/tests_session.cpp
    tests/tests_world.cpp
    tests/tests_storage.cpp
    tests/tests_content.cpp
    tests/tests_store.cpp
    tests/tests_damage.cpp
)
target_link_libraries(coop_tests PRIVATE coop_core)
add_test(NAME coop_tests COMMAND coop_tests)
