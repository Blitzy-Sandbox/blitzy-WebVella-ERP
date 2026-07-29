<!--{"sort_order":1, "name": "overview", "label": "Overview"}-->
# Overview

The Web API gives you access to the content management features you see in your web application and lets you extend them for use in your own applications. It is a RESTful and is organized around the content types and functionalities, of which you are familiar with in the WebVella ERP software.

The WebVella ERP Web API is work in progress and we will gradually implement all available features.

> **Note:** This page documents the **current (legacy) `/api/v3/` Web API**. A canonical REST reference for the **planned** headless `/api/v1/` surface is being authored under [`../../api-reference/index.md`](../../api-reference/index.md) (and, for authentication, [`../../api-reference/authentication.md`](../../api-reference/authentication.md)); that `/api/v1/` surface does not exist in the current checkout.

## Date Format

All dates (both those sent in requests and those returned in responses) should be formatted as presented in the examples. We support dates formatted ISO 8601 String. All dates are and should be in UTC time zone. (ex. "2013-02-04T22:44:30.652Z")

## CORS

CORS, or cross-origin resource sharing, is a way to make XMLHttpRequests to another domain, different from the one that the script is loaded from. CORS is supported in most modern browsers.

## API changes

All extensions of the API will be added only to the latest supported version. Bug fixes and optimizations will be applied to all relevant API versions. The API version is part of the base URL, so you will be able to choose which of API version you use for each of your requests. The current supported version is `v3` (base path `/api/v3/`).

> **Planned (headless refactor — not yet implemented).** The target headless surface is planned to be versioned `/api/v1/`; it does not exist in the current checkout.

## API Base URL

You can make your RESTful requests by adding to your WebVella ERP install domain the API path, based on the API version, locale, content item and methods. It should look like similarly to the following example:

```http
https://<YOUR_DOMAIN>/api/v3/en_US/meta/relation
```

Source: /WebVella.Erp.Web/Controllers/WebApiController.cs:L2036 maps the current `POST api/v3/en_US/meta/relation` route.

> **Planned (headless refactor — not yet implemented).** Under the target headless surface this is planned to become `https://<host>/api/v1/meta/relation`; that route does not exist in the current checkout.

**IMPORTANT:** Secure certificate (https) is recommendable for the WebVella Erp Web API

## Authorization

In order to provide the same level of security that we provide on our web software, many API requests are requiring authorization. In the current codebase this is done by a authorization cookie (`erp_auth_base`).

Source: /WebVella.Erp.Site/Startup.cs:L96 sets `options.Cookie.Name = "erp_auth_base";`.

> **Planned (headless refactor — not yet implemented).** The target headless `/api/v1/` surface is planned to require an OIDC-issued JSON Web Token (JWT) presented as an HTTP `Authorization: Bearer <token>` header instead of the cookie. For the planned authentication reference (obtaining tokens, JWT validation, scopes, and claim-to-role/permission mapping), see [`../../api-reference/authentication.md`](../../api-reference/authentication.md).