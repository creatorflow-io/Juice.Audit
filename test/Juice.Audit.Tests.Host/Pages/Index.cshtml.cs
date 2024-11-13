using Juice.Audit.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Juice.Audit.Tests.Host.Pages
{
    [AccessLogging(Name = "TestAction")]
    [TimeLogging]
    public class IndexModel : PageModel
    {
        public void OnGet()
        {
        }
    }
}
