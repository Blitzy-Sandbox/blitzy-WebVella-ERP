<!--{"sort_order":4, "name": "site-page", "label": "Site page"}-->
> **Deprecated (RazorPages host retired).** This page documents the legacy RazorPages host UI, which is retired in the headless refactor. The underlying **Entity / Record / EQL / hook** model is **unchanged**. For the target UI, see the migration guides: [RazorPages → React](../../migration/razorpages-to-react.md) and [Migration overview](../../migration/overview.md).

# Site page

A site page is a general page that is not related to any application. Often used for general information or help. Such pages can be accessed by following routing `/s/{PageName?}`. If you open the url without specifying `PageName`, the system automatically will redirect to the site page with the smallest sort weight.

All site pages are sorted based on their sort weight and presented in a dropdown menu as presented on the next image.

![sdk site page shortcut](/doc-images/sdk-site-shortcut.png)