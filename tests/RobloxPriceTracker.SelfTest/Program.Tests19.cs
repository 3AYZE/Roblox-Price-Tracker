using System.Text.Json;
using RobloxPriceTracker.Infrastructure;

internal static partial class Program
{
    static Task TestThumbnailCompletedAndPendingStatesAsync()
    {
        const string json = """
        {"data":[
          {"targetId":101,"state":"Completed","imageUrl":"https://tr.rbxcdn.com/one.png"},
          {"targetId":102,"state":"Pending","imageUrl":"https://tr.rbxcdn.com/two.png"},
          {"targetId":103,"state":"Blocked","imageUrl":"https://tr.rbxcdn.com/three.png"},
          {"targetId":104,"state":"Completed","imageUrl":null},
          {"targetId":105,"state":"Completed","imageUrl":"https://tr.rbxcdn.com/five.png"},
          {"targetId":999,"state":"Completed","imageUrl":"https://tr.rbxcdn.com/foreign.png"}
        ]}
        """;
        using var data = JsonDocument.Parse(json);
        var allowed = new HashSet<long> { 101, 102, 103, 104, 105 };
        var result = RobloxThumbnailResponseParser.ParseCompleted(data.RootElement, allowed);
        AssertEqual(2, result.Count);
        AssertTrue(result.ContainsKey(101));
        AssertTrue(result.ContainsKey(105));
        AssertTrue(!result.ContainsKey(102));
        AssertTrue(!result.ContainsKey(103));
        AssertTrue(!result.ContainsKey(104));
        AssertTrue(!result.ContainsKey(999));
        return Task.CompletedTask;
    }

    static Task TestThumbnailRejectsUntrustedSourcesAsync()
    {
        const string json = """
        {"data":[
          {"targetId":1,"state":"Completed","imageUrl":"http://tr.rbxcdn.com/image.png"},
          {"targetId":2,"state":"Completed","imageUrl":"https://roblox.example.com/image.png"},
          {"targetId":3,"state":"Completed","imageUrl":"https://rbxcdn.com.attacker.test/x.png"},
          {"targetId":4,"state":"Completed","imageUrl":"file:///C:/file.png"},
          {"targetId":5,"state":"Completed","imageUrl":"https://t0.rbxcdn.com/image.png"},
          {"targetId":6,"state":"Completed","imageUrl":"https://thumbnails.roblox.com/image.png"}
        ]}
        """;
        using var data = JsonDocument.Parse(json);
        var result = RobloxThumbnailResponseParser.ParseCompleted(data.RootElement,
            new HashSet<long> { 1, 2, 3, 4, 5, 6 });
        AssertEqual(2, result.Count);
        AssertTrue(result.ContainsKey(5));
        AssertTrue(result.ContainsKey(6));
        return Task.CompletedTask;
    }

    static Task TestThumbnailMalformedOrMissingDataAsync()
    {
        foreach (var json in new[] { "{}", "{\"data\":null}", "{\"data\":[]}" })
        {
            using var data = JsonDocument.Parse(json);
            var result = RobloxThumbnailResponseParser.ParseCompleted(data.RootElement,
                new HashSet<long> { 1 });
            AssertEqual(0, result.Count);
        }
        return Task.CompletedTask;
    }
}
