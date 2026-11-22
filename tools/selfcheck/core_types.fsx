// The type definitions, type references and assembly references of one .NET assembly, a line each, for
// core_regression.sh: "def <namespace>.<name>", "ref <namespace>.<name>", "asm <name>". Run with dotnet fsi.
open System.IO
open System.Reflection.Metadata
open System.Reflection.PortableExecutable

let pe = new PEReader(File.OpenRead(fsi.CommandLineArgs.[1]))
let md = pe.GetMetadataReader()
for h in md.TypeDefinitions do
    let t = md.GetTypeDefinition h
    printfn "def %s.%s" (md.GetString t.Namespace) (md.GetString t.Name)
for h in md.TypeReferences do
    let t = md.GetTypeReference h
    printfn "ref %s.%s" (md.GetString t.Namespace) (md.GetString t.Name)
for h in md.AssemblyReferences do
    printfn "asm %s" (md.GetString (md.GetAssemblyReference h).Name)
