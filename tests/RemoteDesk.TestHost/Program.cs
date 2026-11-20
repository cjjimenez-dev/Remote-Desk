using SIPSorceryMedia.Abstractions;

var members = typeof(VideoSample).GetMembers();
foreach (var m in members)
{
    Console.WriteLine($"MEMBER: {m.MemberType} {m.Name}");
}
