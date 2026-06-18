using System.Runtime.CompilerServices;

// Lets the test project drive internal types (TreasureMaster, IGameMemory, Offsets).
[assembly: InternalsVisibleTo("FFTTreasureMaster.Tests")]
