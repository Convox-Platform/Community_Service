using Community_Service.Auth;
using Community_Service.Data;
using Community_Service.Permissions;
using Grpc.Core;

namespace Community_Service.Services
{
    public class CategoryGrpcService : CategoryService.CategoryServiceBase
    {
        private readonly CategoryRepository _categories;
        private readonly IPermissionGuard _guard;

        public CategoryGrpcService(CategoryRepository categories, IPermissionGuard guard)
        {
            _categories = categories;
            _guard = guard;
        }

        public override async Task<CreateCategoryResponse> CreateCategory(
            CreateCategoryRequest request, ServerCallContext context)
        {
            var userId = context.GetUserId();
            var communityId = (long)request.CommunityId;
            await _guard.EnsureCanManageChannelsAsync(userId, communityId);

            if (string.IsNullOrWhiteSpace(request.Name))
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Name is required"));

            var category = await _categories.CreateAsync(communityId, request.Name, userId);
            return new CreateCategoryResponse { Category = category.ToProto() };
        }

        public override async Task<EditCategoryResponse> EditCategory(
            EditCategoryRequest request, ServerCallContext context)
        {
            var userId = context.GetUserId();
            var communityId = (long)request.CommunityId;
            await _guard.EnsureCanManageChannelsAsync(userId, communityId);

            await LoadCategoryAsync((long)request.CategoryId, communityId);

            var category = await _categories.UpdateAsync(
                (long)request.CategoryId,
                request.HasName ? request.Name : null,
                null,
                userId,
                request.HasName ? ["name"] : []);

            return new EditCategoryResponse { Category = category.ToProto() };
        }

        public override async Task<RemoveCategoryResponse> RemoveCategory(
            RemoveCategoryRequest request, ServerCallContext context)
        {
            var userId = context.GetUserId();
            var communityId = (long)request.CommunityId;
            await _guard.EnsureCanManageChannelsAsync(userId, communityId);

            var category = await LoadCategoryAsync((long)request.CategoryId, communityId);
            await _categories.DeleteAsync(category.Id, userId);
            return new RemoveCategoryResponse();
        }

        private async Task<CategoryEntity> LoadCategoryAsync(long categoryId, long communityId)
        {
            var category = await _categories.GetByIdAsync(categoryId);
            if (category is null || category.CommunityId != communityId)
                throw new RpcException(new Status(StatusCode.NotFound, "Category not found"));
            return category;
        }
    }
}
