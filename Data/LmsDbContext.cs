using Microsoft.EntityFrameworkCore;
using MarkIasVideoProcessingApi.Models;

namespace MarkIasVideoProcessingApi.Data;

public class MarkIasVideoProcessingDbContext(DbContextOptions<MarkIasVideoProcessingDbContext> options) : DbContext(options)
{
    public DbSet<Video> Videos => Set<Video>();
}
