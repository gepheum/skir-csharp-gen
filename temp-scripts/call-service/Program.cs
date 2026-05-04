using System.Collections.Immutable;
using SkirClient;
using Skirout_Service;

// Sends RPCs to the SkirRPC service. See start-service for how to start it.
//
// Run with:
//   dotnet run --project temp-scripts/call-service/call-service.csproj
//
// Make sure the service is running first (using start-service).

var client = new ServiceClient("http://localhost:8787/myapi");

// Add two users.
var users = new[]
{
    new User
    {
        UserId = 42,
        Name = "John Doe",
        Quote = "Coffee is just a socially acceptable form of rage.",
        Pets = ImmutableList<User_Pet>.Empty,
        SubscriptionStatus = SubscriptionStatus.Free,
    },
    Consts.Tarzan,
};

foreach (var user in users)
{
    string name = user.Name;
    int id = user.UserId;

    await client.InvokeRemote(
        Methods.AddUser,
        new AddUserRequest { User = user });

    Console.WriteLine($"Added user {name:?} (id={id})");
}

// Retrieve Tarzan.
var tarzan = Consts.Tarzan;
var resp = await client.InvokeRemote(
    Methods.GetUser,
    new GetUserRequest { UserId = tarzan.UserId });

if (resp.User is User userResp)
{
    Console.WriteLine($"Got user: {User.Serializer.ToJson(userResp, readable: true)}");
}
else
{
    Console.WriteLine("User not found");
}
