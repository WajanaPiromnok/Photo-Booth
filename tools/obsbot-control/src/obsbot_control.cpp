#include <algorithm>
#include <chrono>
#include <cstdlib>
#include <iostream>
#include <list>
#include <memory>
#include <string>
#include <thread>

#include <dev/devs.hpp>

namespace {

constexpr int kDefaultTimeoutMs = 8000;
constexpr int kPollIntervalMs = 250;

void printUsage() {
    std::cerr
        << "Usage: obsbot-control <wake|sleep|privacy|status> [--device-name <name>] [--device-sn <sn>] [--timeout-ms <ms>]\n"
        << "\n"
        << "Controls OBSBOT hardware run state through libdev. This does not toggle OBSBOT Center Virtual Camera.\n";
}

std::string lower(std::string value) {
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char ch) {
        return static_cast<char>(std::tolower(ch));
    });
    return value;
}

std::shared_ptr<Device> findDevice(const std::string &deviceName, const std::string &deviceSn, int timeoutMs) {
    Devices::get().setEnableMdnsScan(false);

    const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(timeoutMs);
    do {
        auto devices = Devices::get().getDevList();
        if (!deviceSn.empty()) {
            auto device = Devices::get().getDevBySn(deviceSn);
            if (device) {
                return device;
            }
        }

        if (!deviceName.empty()) {
            const auto expected = lower(deviceName);
            for (const auto &device : devices) {
                if (device && lower(device->devName()).find(expected) != std::string::npos) {
                    return device;
                }
            }
        }

        if (!devices.empty()) {
            return devices.front();
        }

        std::this_thread::sleep_for(std::chrono::milliseconds(kPollIntervalMs));
    } while (std::chrono::steady_clock::now() < deadline);

    return nullptr;
}

int setRunState(const std::shared_ptr<Device> &device, Device::DevStatus status) {
    const auto result = device->cameraSetDevRunStatusR(status);
    if (result != 0) {
        std::cerr << "OBSBOT command failed: result=" << result << "\n";
        return 2;
    }

    std::cout << "OBSBOT command sent: device=" << device->devName()
              << ", sn=" << device->devSn()
              << ", status=" << static_cast<int>(status) << "\n";
    return 0;
}

} // namespace

int main(int argc, char **argv) {
    if (argc < 2) {
        printUsage();
        return 64;
    }

    std::string command = lower(argv[1]);
    std::string deviceName;
    std::string deviceSn;
    int timeoutMs = kDefaultTimeoutMs;

    for (int i = 2; i < argc; ++i) {
        std::string arg = argv[i];
        if (arg == "--device-name" && i + 1 < argc) {
            deviceName = argv[++i];
        } else if (arg == "--device-sn" && i + 1 < argc) {
            deviceSn = argv[++i];
        } else if (arg == "--timeout-ms" && i + 1 < argc) {
            timeoutMs = std::max(250, std::atoi(argv[++i]));
        } else {
            printUsage();
            return 64;
        }
    }

    auto device = findDevice(deviceName, deviceSn, timeoutMs);
    if (!device) {
        std::cerr << "No OBSBOT device found.\n";
        return 1;
    }

    int exitCode = 0;
    if (command == "wake" || command == "run") {
        exitCode = setRunState(device, Device::DevStatusRun);
    } else if (command == "sleep") {
        exitCode = setRunState(device, Device::DevStatusSleep);
    } else if (command == "privacy") {
        exitCode = setRunState(device, Device::DevStatusPrivacy);
    } else if (command == "status") {
        std::cout << "OBSBOT device: name=" << device->devName()
                  << ", sn=" << device->devSn()
                  << ", version=" << device->devVersion()
                  << ", productType=" << static_cast<int>(device->productType())
                  << ", mode=" << static_cast<int>(device->devMode()) << "\n";
    } else {
        printUsage();
        exitCode = 64;
    }

    return exitCode;
}
