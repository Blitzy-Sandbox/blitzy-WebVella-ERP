/**
 * @fileoverview CDK Stack for S3 static website hosting of the WebVella ERP React SPA.
 *
 * This stack replaces the monolith's ASP.NET Core `UseStaticFiles()` middleware
 * (Startup.cs lines 166-176) which served static assets from wwwroot with
 * cache-control headers (`public,max-age=31536000`).
 *
 * Architecture decisions per AAP:
 * - §0.8.1: Pure static SPA — zero SSR, zero Lambda@Edge, zero API routes
 * - §0.7.6: Dual-target — S3 website hosting for LocalStack, S3 + CloudFront for production
 * - §0.3.2: CloudFront skipped entirely in LocalStack mode
 * - §0.8.2: Performance targets — TTI < 2s on 4G, chunks < 200KB gzipped
 * - §0.8.3: Encryption at rest (S3 managed), TLS in transit via CloudFront
 * - §0.8.6: API Gateway URL stored in SSM for VITE_API_URL build injection
 *
 * Resources created:
 * - S3 Bucket with static website hosting (index.html for both index and error document)
 * - Bucket policy for public read access (s3:GetObject via StarPrincipal)
 * - Production-only bucket policy for authenticated bucket discovery (AnyPrincipal)
 * - SSM Parameters for frontend URL and API Gateway URL service discovery
 * - CloudFront Distribution with SPA error response handling (production only)
 *
 * @module FrontendStack
 */

import * as cdk from 'aws-cdk-lib';
import { Construct } from 'constructs';
import * as s3 from 'aws-cdk-lib/aws-s3';
import * as ssm from 'aws-cdk-lib/aws-ssm';
import * as cloudfront from 'aws-cdk-lib/aws-cloudfront';
import * as origins from 'aws-cdk-lib/aws-cloudfront-origins';
import * as iam from 'aws-cdk-lib/aws-iam';

/**
 * Properties for configuring the FrontendStack.
 *
 * Extends standard CDK StackProps with parameters specific to the
 * WebVella ERP frontend deployment, supporting dual-target deployment
 * to both LocalStack (development) and production AWS environments.
 */
export interface FrontendStackProps extends cdk.StackProps {
  /**
   * Whether this stack is being deployed to LocalStack.
   *
   * When true:
   * - CloudFront distribution is skipped (per AAP §0.7.6)
   * - S3 bucket uses DESTROY removal policy for easy cleanup
   * - Auto-delete objects is enabled for clean teardown
   * - Bucket versioning is disabled
   *
   * When false:
   * - CloudFront distribution is created for CDN caching and HTTPS
   * - S3 bucket uses RETAIN removal policy for data safety
   * - Bucket versioning is enabled for rollback support
   * - Lifecycle rules clean up noncurrent versions after 90 days
   */
  readonly isLocalStack: boolean;

  /**
   * The HTTP API Gateway v2 URL for backend services.
   *
   * Stored in SSM Parameter Store at `/webvella-erp/frontend/api-url`
   * so that deployment scripts can inject it as `VITE_API_URL` during
   * the frontend Vite build process (per AAP §0.8.6).
   *
   * Example: `https://abc123.execute-api.us-east-1.amazonaws.com`
   * LocalStack: `http://localhost:4566/restapis/<id>/prod/_user_request_`
   */
  readonly apiGatewayUrl: string;
}

/**
 * CDK Stack for hosting the WebVella ERP React 19 SPA frontend.
 *
 * Creates an S3 bucket configured for static website hosting that serves the
 * Vite 6-built React SPA. In production mode, a CloudFront distribution is
 * added for HTTPS termination, global edge caching, and proper SPA routing
 * via custom error responses (403/404 → /index.html with HTTP 200).
 *
 * This stack replaces the monolith's static file serving pipeline:
 * - ASP.NET Core `UseStaticFiles()` with 1-year cache headers
 * - Bootstrap 4 CSS + jQuery JS bundles from wwwroot
 * - StencilJS web component bundles from plugin wwwroot directories
 *
 * The React SPA served from this bucket replaces all of:
 * - 16 Razor Pages (route structure → React Router 7)
 * - 50+ ViewComponents (UI rendering → React components)
 * - jQuery DOM manipulation → React state management
 * - StencilJS web components → React components with Tailwind CSS 4
 *
 * @example
 * ```typescript
 * const frontendStack = new FrontendStack(app, 'FrontendStack', {
 *   isLocalStack: true,
 *   apiGatewayUrl: 'http://localhost:4566/restapis/abc123/prod/_user_request_',
 *   env: { account: '000000000000', region: 'us-east-1' },
 * });
 * ```
 */
export class FrontendStack extends cdk.Stack {
  /**
   * The name of the S3 bucket hosting the React SPA frontend assets.
   * Use this to configure deployment scripts that sync built assets to S3.
   */
  public readonly frontendBucketName: string;

  /**
   * The primary URL for accessing the frontend.
   *
   * - In LocalStack mode: S3 website hosting URL (HTTP)
   * - In production mode: CloudFront distribution URL (HTTPS)
   */
  public readonly frontendUrl: string;

  /**
   * The CloudFront distribution URL for production deployments.
   * Empty string when `isLocalStack` is true (no CloudFront created).
   */
  public readonly distributionUrl: string;

  constructor(scope: Construct, id: string, props: FrontendStackProps) {
    super(scope, id, props);

    const { isLocalStack, apiGatewayUrl } = props;

    // =========================================================================
    // PROTOCOL RESOLUTION
    // =========================================================================
    // Determine the appropriate protocol for frontend URL resolution.
    // LocalStack uses HTTP (S3 website hosting), production uses HTTPS (CloudFront).
    // This mirrors the monolith's dual serving mode: HTTP in dev, HTTPS in prod.
    const websiteProtocol: s3.RedirectProtocol = isLocalStack
      ? s3.RedirectProtocol.HTTP
      : s3.RedirectProtocol.HTTPS;

    // =========================================================================
    // S3 BUCKET — Static Website Hosting for React SPA
    // =========================================================================
    // Replaces ASP.NET Core UseStaticFiles() from Startup.cs (lines 166-176)
    // which served static assets with cache-control: public,max-age=31536000
    // and ServeUnknownFileTypes = false.
    //
    // SPA routing strategy: Both websiteIndexDocument and websiteErrorDocument
    // are set to 'index.html' so that all paths resolve to the React app,
    // which then uses React Router 7 for client-side route matching.
    const websiteBucket = new s3.Bucket(this, 'FrontendBucket', {
      // SPA routing: both index and error documents point to index.html
      // so React Router 7 handles all client-side routes
      websiteIndexDocument: 'index.html',
      websiteErrorDocument: 'index.html',

      // Public access must be unblocked for S3 static website hosting.
      // S3 website endpoints require public read access to serve content
      // to browsers — this replaces the monolith's Kestrel HTTP serving.
      blockPublicAccess: new s3.BlockPublicAccess({
        blockPublicAcls: false,
        blockPublicPolicy: false,
        ignorePublicAcls: false,
        restrictPublicBuckets: false,
      }),

      // S3-managed encryption at rest (AAP §0.8.3 security requirement)
      encryption: s3.BucketEncryption.S3_MANAGED,

      // Removal policy varies by environment:
      // DESTROY for LocalStack: enables clean teardown during development
      // RETAIN for production: prevents accidental data loss during stack updates
      removalPolicy: isLocalStack
        ? cdk.RemovalPolicy.DESTROY
        : cdk.RemovalPolicy.RETAIN,

      // Auto-delete objects only in LocalStack mode for clean teardown
      // when running `cdklocal destroy` — prevents orphaned buckets
      autoDeleteObjects: isLocalStack,

      // Enable versioning in production for rollback capability
      // Disabled in LocalStack to reduce storage overhead during development
      versioned: !isLocalStack,

      // CORS configuration for SPA asset loading.
      // Mirrors the AllowAnyOrigin CORS policy from Startup.cs (lines 73-74)
      // which used `policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()`
      cors: [
        {
          allowedMethods: [s3.HttpMethods.GET],
          allowedOrigins: ['*'],
          allowedHeaders: ['*'],
          maxAge: 3600,
        },
      ],

      // Lifecycle rules for managing storage costs in production.
      // Noncurrent versions (from versioning) are cleaned up after 90 days.
      // No lifecycle rules needed in LocalStack (versioning disabled).
      lifecycleRules: isLocalStack
        ? []
        : [
            {
              id: 'CleanupNoncurrentVersions',
              enabled: true,
              noncurrentVersionExpiration: cdk.Duration.days(90),
            },
          ],
    });

    // =========================================================================
    // BUCKET POLICY — Public Read Access for Static Website Hosting
    // =========================================================================
    // S3 static website hosting requires a bucket policy allowing public
    // s3:GetObject access. This allows browsers to fetch HTML, JS, CSS,
    // and image assets directly from the S3 website endpoint.
    //
    // Uses StarPrincipal ("Principal": "*") to allow access from any source,
    // including unauthenticated/anonymous requests — required for public
    // website hosting per AWS best practices.
    websiteBucket.addToResourcePolicy(
      new iam.PolicyStatement({
        sid: 'AllowPublicWebsiteRead',
        effect: iam.Effect.ALLOW,
        principals: [new iam.StarPrincipal()],
        actions: ['s3:GetObject'],
        resources: [`${websiteBucket.bucketArn}/*`],
      })
    );

    // Production-only: Allow authenticated AWS principals to discover
    // bucket location. This enables CDK deployment tooling, CI/CD scripts,
    // and cross-region configurations to resolve the bucket's region.
    // Uses AnyPrincipal ("Principal": {"AWS": "*"}) for AWS-authenticated access.
    if (!isLocalStack) {
      websiteBucket.addToResourcePolicy(
        new iam.PolicyStatement({
          sid: 'AllowAuthenticatedBucketDiscovery',
          effect: iam.Effect.ALLOW,
          principals: [new iam.AnyPrincipal()],
          actions: ['s3:GetBucketLocation'],
          resources: [websiteBucket.bucketArn],
        })
      );
    }

    // =========================================================================
    // SSM PARAMETERS — Service Discovery
    // =========================================================================
    // Store URLs in SSM Parameter Store for other services and deployment
    // scripts to discover the frontend hosting location.

    // Frontend website URL — used by other stacks and health checks
    new ssm.StringParameter(this, 'FrontendUrlParameter', {
      parameterName: '/webvella-erp/frontend/url',
      stringValue: websiteBucket.bucketWebsiteUrl,
      description: 'Frontend SPA website URL for the WebVella ERP platform',
    });

    // API Gateway URL — used during `vite build` to inject VITE_API_URL
    // Deployment scripts read this parameter and set it as an environment
    // variable before running the Vite production build (AAP §0.8.6).
    new ssm.StringParameter(this, 'FrontendApiUrlParameter', {
      parameterName: '/webvella-erp/frontend/api-url',
      stringValue: apiGatewayUrl,
      description:
        'API Gateway URL injected as VITE_API_URL during frontend build',
    });

    // =========================================================================
    // CLOUDFRONT DISTRIBUTION — Production Only (AAP §0.7.6)
    // =========================================================================
    // In production, a CloudFront distribution is placed in front of the S3
    // bucket to provide:
    // - HTTPS termination (TLS 1.3 per AAP §0.8.3)
    // - Global edge caching for < 2s TTI target (AAP §0.8.2)
    // - Custom error responses for SPA client-side routing
    //
    // Skipped entirely in LocalStack mode per AAP §0.3.2 (CloudFront is
    // not supported in LocalStack and is explicitly out of scope).
    let distributionDomainUrl = '';

    if (!isLocalStack) {
      // Define SPA error responses: redirect 403/404 to index.html with 200 status.
      // This ensures deep links work correctly — when a user navigates directly
      // to /crm/contacts/123, CloudFront returns index.html and React Router
      // handles the route match client-side.
      //
      // 403 errors occur when S3 returns AccessDenied for non-existent paths
      // (without s3:ListBucket permission, S3 returns 403 instead of 404).
      // 404 errors occur for genuinely missing files.
      const spaErrorResponses: cloudfront.ErrorResponse[] = [
        {
          httpStatus: 403,
          responseHttpStatus: 200,
          responsePagePath: '/index.html',
          ttl: cdk.Duration.days(0),
        },
        {
          httpStatus: 404,
          responseHttpStatus: 200,
          responsePagePath: '/index.html',
          ttl: cdk.Duration.days(0),
        },
      ];

      const distribution = new cloudfront.Distribution(
        this,
        'FrontendDistribution',
        {
          // Use S3StaticWebsiteOrigin to connect to the S3 website endpoint
          // (not the REST API endpoint) — this supports index.html fallback
          // and custom error documents configured on the bucket.
          defaultBehavior: {
            origin: new origins.S3StaticWebsiteOrigin(websiteBucket),
            viewerProtocolPolicy:
              cloudfront.ViewerProtocolPolicy.REDIRECT_TO_HTTPS,
            cachePolicy: cloudfront.CachePolicy.CACHING_OPTIMIZED,
          },
          defaultRootObject: 'index.html',
          errorResponses: spaErrorResponses,
        }
      );

      distributionDomainUrl = `https://${distribution.distributionDomainName}`;

      // Tag the CloudFront distribution for resource management
      cdk.Tags.of(distribution).add('Service', 'frontend');
      cdk.Tags.of(distribution).add('Environment', 'production');
      cdk.Tags.of(distribution).add('ManagedBy', 'CDK');
    }

    // =========================================================================
    // RESOURCE TAGGING
    // =========================================================================
    // Apply consistent tags to all resources for cost allocation,
    // operational visibility, and resource management.
    cdk.Tags.of(websiteBucket).add('Service', 'frontend');
    cdk.Tags.of(websiteBucket).add(
      'Environment',
      isLocalStack ? 'local' : 'production'
    );
    cdk.Tags.of(websiteBucket).add('ManagedBy', 'CDK');
    cdk.Tags.of(this).add('Stack', 'FrontendStack');

    // =========================================================================
    // STACK OUTPUTS & PUBLIC PROPERTIES
    // =========================================================================
    // Resolve the final frontend URL based on the deployment target.
    // LocalStack: S3 website URL (HTTP) since CloudFront is unavailable.
    // Production: CloudFront distribution URL (HTTPS) for secure, cached access.
    const resolvedFrontendUrl =
      websiteProtocol === s3.RedirectProtocol.HTTPS && distributionDomainUrl
        ? distributionDomainUrl
        : websiteBucket.bucketWebsiteUrl;

    // Set public readonly properties for cross-stack references
    this.frontendBucketName = websiteBucket.bucketName;
    this.frontendUrl = resolvedFrontendUrl;
    this.distributionUrl = distributionDomainUrl;

    // Export values via CfnOutput for CLI visibility and cross-stack references
    new cdk.CfnOutput(this, 'FrontendBucketNameOutput', {
      value: this.frontendBucketName,
      description: 'S3 bucket name hosting the React SPA frontend assets',
      exportName: `${this.stackName}-FrontendBucketName`,
    });

    new cdk.CfnOutput(this, 'FrontendUrlOutput', {
      value: this.frontendUrl,
      description: 'Primary URL for the React SPA frontend',
      exportName: `${this.stackName}-FrontendUrl`,
    });

    new cdk.CfnOutput(this, 'DistributionUrlOutput', {
      value: this.distributionUrl || 'N/A (LocalStack mode - no CloudFront)',
      description: 'CloudFront distribution URL (production only)',
      exportName: `${this.stackName}-DistributionUrl`,
    });
  }
}
