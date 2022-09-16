# Warpinator as a library for multi-plateform (unofficial)

This is an unofficial reimplementation of Linux Mint's file sharing tool [Warpinator](https://github.com/linuxmint/warpinator) for Windows 7-10.

- The idea is to break the implementation into two layers(two projects)  : 
  1) A library ( iShare.dll)  that can be compiled and used for diffrent plateforms,
  2) and a windows graphical application  that uses this library.


## Download Releases
not yet available 

## Building
Requires .NET SDK 6.0.401

Build with Visual Studio

### Screenshot
![screenshot](screenshot.png)

## Translating
You will need a recent version of Visual Studio
1) Create a new Resource file in the Resources folder called Strings.xx.resx where xx is code of the language you are translating to
2) Copy the entire table from Strings.resx and translate the values. Comments are only for context
3) Open Controls\TransferPanel, Form1, SettingsForm and TransferFrom in designer and repeat 4-6 on each of them
4) Select the toplevel element (whole window) and under Properties switch Language to your language
5) Select controls with text on them (buttons, labels, menus) and translate their "Text" property. You don't need to translate obvious placeholders that will be replaced at runtime. Can be verified by simply running the application (green play arrow in toolbar). Also, two buttons on TransferPanel are hidden below the other two.
6) You can also move and resize the controls to fit the new strings and it will only affect the currently selected language
