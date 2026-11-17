# TransparentSights

An SPT client mod for transparent sights while aiming. This fork of [7Bpencil’s TransparentSights](https://github.com/7Bpencil/SPT.TransparentSights) adds an optional blur layer for the sight housing, with independent controls for housing opacity and weapon blur.

## Install

1. Download the ZIP from [Releases](https://github.com/orest-kavun/SPT.TransparentSights/releases).
2. Close the game and extract the ZIP into your SPT game folder. If you have the original mod, replace its DLL and keep your existing settings.
3. In the F12 menu, enable **Housing Layer → Separate housing blur**. Tune **Housing blur radius** and **Housing opacity** there. **General → Blur transparent sights** enables camera depth of field for the weapon, with strength and spread under **Depth of Field** and **Depth of Field Advanced**.

You can still choose transparency for each sight in its item inspection panel. The separate housing layer activates when the mod recognizes the sight’s housing materials.

## Compatibility

Built against SPT 4.1.6 game files. The separate layer was tested on the Aimpoint CompM4 with FSR4 in earlier builds. The 0.2.27 menu cleanup is a pre-release pending an in-game check.

## Build

Build `Client/TransparentSights.csproj` with the .NET SDK. Pass `SPTPath` as the path to your SPT game folder. Set `OutPath` and `StagePath` to build and packaging folders if you want to stage the result outside your installation.

## License

[MIT](LICENSE). Original project by [7Bpencil](https://github.com/7Bpencil).
