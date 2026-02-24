using FileMoverWeb.Services;

var builder = WebApplication.CreateBuilder(args);

// 1) Controllers（讓 JobsController 生效）
builder.Services.AddControllers();

// 2) 你的服務（TaskPoller 會用到 IConfiguration + ILogger）
builder.Services.AddScoped<TaskPoller>();

var app = builder.Build();

// 3) 靜態檔案（讓 wwwroot/index.html 可開）
app.UseDefaultFiles();
app.UseStaticFiles();

// 4) 路由到 Controllers
app.MapControllers();

app.Run();