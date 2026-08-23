namespace JarvisAI.Application.Security;

public interface IUserConfirmationService
{
    Task<ConfirmationResult> RequestConfirmationAsync(ConfirmationRequest request, CancellationToken cancellationToken = default);
}
