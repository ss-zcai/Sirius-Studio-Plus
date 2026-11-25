using Sirius.Tools.ShotSample;

// Writes a sample project for trying several shots a part (F26) end to end:
// pictures in sets of three, a recipe that judges a part from its three, a
// station of 3 shots a part, and a simulated PLC with the 3-shot starter.
//
//   dotnet run --project tools/ShotSample                       -> Documents\Sirius Samples
//   dotnet run --project tools/ShotSample -- D:\Samples
//   dotnet run --project tools/ShotSample -- D:\Samples --images D:\MyShots
//
// --images uses a folder of your own pictures instead of the made-up ones:
// named so they sort in sets (part01_1, part01_2, part01_3, part02_1, ...).

string? parent = null;
string? images = null;

for (var i = 0; i < args.Length; i++)
{
    if (args[i] is "--images" or "-i" && i + 1 < args.Length)
        images = args[++i];
    else if (args[i] is "--help" or "-h" or "/?")
    {
        Console.WriteLine("ShotSample [parent folder] [--images <folder of your sets>]");
        return 0;
    }
    else
        parent = args[i];
}

parent ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Sirius Samples");

try
{
    var written = ShotSampleProject.Write(parent, images);
    Console.WriteLine($"Written: {written.ProjectFile}");
    Console.WriteLine($"Pictures: {written.Images} ({written.ImageCount} images, {written.ImageCount / ShotSampleProject.Shots} parts)");
    Console.WriteLine("Read README.txt beside the project for what to try.");
    return 0;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
