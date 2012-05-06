# Releases
All releases are available as source tags on GitHub.

## v1.x
* v1.0 runs under [Aurora-Sim 5.0.1](https://github.com/aurora-sim/Aurora-Sim/downloads)
* v1.1.1 and newer run under [Aurora-Sim master](https://github.com/aurora-sim/Aurora-Sim/tree/master)
* v1.x releases work with [Aurora-WebUI frozen branch](https://github.com/aurora-sim/Aurora-WebUI/tree/frozen)

## v2.x

### Release Candidates
* No v2.x release candidate works with Aurora-Sim 5.0.1
* v2.x release candidates only run under [Aurora-Sim master](https://github.com/aurora-sim/Aurora-Sim/tree/master)
* v2.x release candidates work with [libAurora.php](https://github.com/aurora-sim/libAurora.php), [Aurora-WebUI master branch](https://github.com/aurora-sim/Aurora-WebUI/tree/master) and [Aurora-WebUI-GPL](https://github.com/SignpostMarv/Aurora-WebUI-GPL)

# Installation

## Install WebUI via console
1. Start Aurora.Server.exe (if you want to run in Grid mode) or Aurora.exe (if you want to run in StandAlone mode)
2. Put into the console 'compile module gui' and browse to the WebUI directory in your Aurora-WebAPI download and open the build.am file.
3. Follow the instructions on-screen and it will compile and install your module and you are all done with setup.

## Install WebUI manually
1. copy the WebUI directory into your ~/Aurora-Sim/addon-modules/ directory
2. Run runprebuild.bat

# Configuration

## For grid mode (running Aurora.Server.exe)
Copy WebUI/WebUI.ini to your ~/Aurora-Sim/bin/ directory/AuroraServerConfiguration/Modules directory

## For standalone mode (just running Aurora.exe)
Copy WebUI/WebUI.ini to your ~/Aurora-Sim/bin/ directory/Configuration/Modules directory

# Notes

* DeleteUser currently performs a perma-ban action instead of removing the user from the database.
