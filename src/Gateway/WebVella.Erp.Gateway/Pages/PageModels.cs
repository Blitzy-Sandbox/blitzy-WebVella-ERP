// Minimal page model stubs inheriting from BaseErpPageModel to satisfy Razor compilation.
// These will be fully implemented by their assigned agents.
using Microsoft.AspNetCore.Authorization;
using WebVella.Erp.Gateway.Models;

namespace WebVella.Erp.Gateway.Pages
{
    [AllowAnonymous]
    public class LoginModel : BaseErpPageModel
    {
        public void OnGet() { }
        public void OnPost() { }
    }

    // LogoutModel: fully implemented in logout.cshtml.cs
    // HomePageModel: fully implemented in Index.cshtml.cs

    // SitePageModel: fully implemented in Site.cshtml.cs

    // ApplicationHomePageModel: fully implemented in ApplicationHome.cshtml.cs

    // ApplicationNodePageModel: fully implemented in ApplicationNode.cshtml.cs

    public class RecordListPageModel : BaseErpPageModel
    {
        public void OnGet() { }
    }

    public class RecordCreatePageModel : BaseErpPageModel
    {
        public void OnGet() { }
    }

    public class RecordDetailsPageModel : BaseErpPageModel
    {
        public void OnGet() { }
    }

    public class RecordManagePageModel : BaseErpPageModel
    {
        public void OnGet() { }
    }

    public class RecordRelatedRecordsListPageModel : BaseErpPageModel
    {
        public void OnGet() { }
    }

    public class RecordRelatedRecordCreatePageModel : BaseErpPageModel
    {
        public void OnGet() { }
    }

    public class RecordRelatedRecordDetailsPageModel : BaseErpPageModel
    {
        public void OnGet() { }
    }

    public class RecordRelatedRecordManagePageModel : BaseErpPageModel
    {
        public void OnGet() { }
    }
}
