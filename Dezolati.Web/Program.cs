using Dezolati.Web;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

// host z odkazu na akci (anonymní účet) – sdílené Services/Akce.cs ho bere jako „já“
Dezolati.Services.AkceService.GuestUid = () => Dezolati.Services.AuthSession.GuestUid;
// vlastní obrázky výzdoby akce do SVG
Dezolati.Services.SceneKit.ImageHref = key =>
    Dezolati.Services.AkceService.MediaOf(key) is { } m ? $"data:{m.Mime};base64,{m.Data}" : null;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");
await builder.Build().RunAsync();
