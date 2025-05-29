using Juice.Audit.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc;

namespace Juice.Audit.Tests.Host.Controllers
{
    public class TestController: Controller
    {
        [AccessLogging(StatusCodes.Status404NotFound, StatusCodes.Status400BadRequest)]
        public IActionResult ShouldNotFound()
        {
            return NotFound();
        }

        [AccessLogging(StatusCodes.Status404NotFound)]
        public IActionResult ShouldFound()
        {
            return Ok();
        }

        [TimeLogging(1000)]
        public IActionResult ShouldTimeExceed()
        {
            Thread.Sleep(2000);
            return Ok();
        }

        [TimeLogging(1000)]
        public IActionResult ShouldTimeNotExceed()
        {
            Thread.Sleep(500);
            return Ok();
        }
    }
}
