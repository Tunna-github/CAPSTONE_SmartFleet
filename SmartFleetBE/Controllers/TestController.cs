using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using SmartFleetBE.Constants;

namespace SmartFleetBE.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize(Roles = AppRoles.Admin)]
    public class TestController : ControllerBase
    {
        private readonly SmartFleetDbContext _context;

        public TestController(SmartFleetDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public async Task<ActionResult<List<Role>>> Get()
        {
            var testData = await _context.Roles.ToListAsync();
            return Ok(testData);
        }
    }
}
