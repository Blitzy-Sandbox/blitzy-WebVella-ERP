<!--{"sort_order":1, "name": "overview", "label": "Overview"}-->
# Overview

The Web API gives you access to the content management features you see in your web application and lets you extend them for use in your own applications. It is a RESTful and is organized around the content types and functionalities, of which you are familiar with in the WebVella ERP software.

The WebVella ERP Web API is work in progress and we will gradually implement all available features.

> **Note:** This Web API section is superseded by the canonical REST reference. See the API Reference overview at [`../../api-reference/index.md`](../../api-reference/index.md) and, for authentication, [`../../api-reference/authentication.md`](../../api-reference/authentication.md).

## Date Format

All dates (both those sent in requests and those returned in responses) should be formatted as presented in the examples. We support dates formatted ISO 8601 String. All dates are and should be in UTC time zone. (ex. "2013-02-04T22:44:30.652Z")

## CORS

CORS, or cross-origin resource sharing, is a way to make XMLHttpRequests to another domain, different from the one that the script is loaded from. CORS is supported in most modern browsers.

## API changes

All extensions of the API will be added only to the latest supported version. Bug fixes and optimizations will be applied to all relevant API versions. The API version is part of the base URL, so you will be able to choose which of API version you use for each of your requests. The current supported version is `v1` (base path `/api/v1/`).

Source: /docs/developer/web-api/overview.md:L18

## API Base URL

You can make your RESTful requests by adding to your WebVella ERP install domain the API path, based on the API version, content item and methods. It should look like similarly to the following example:

```http
https://<host>/api/v1/meta/relation
```

Source: /docs/developer/web-api/overview.md:L25

**IMPORTANT:** Secure certificate (https) is recommendable for the WebVella Erp Web API

## Authorization

Many API requests require authorization. Authorization is provided by an OIDC-issued JSON Web Token (JWT) presented as an HTTP Bearer token in the `Authorization` header — `Authorization: Bearer <token>` — which replaces the legacy browser-session authorization used by the retired web application.

Source: /docs/developer/web-api/overview.md:L31

For the full authentication reference (obtaining tokens, JWT validation, scopes, and claim-to-role/permission mapping), see [`../../api-reference/authentication.md`](../../api-reference/authentication.md). Do not duplicate those details here.
