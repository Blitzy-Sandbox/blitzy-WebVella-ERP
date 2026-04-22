/**
 * CommentList — `apps/frontend/src/pages/projects/CommentList.tsx`
 *
 * React page component replacing the monolith's `PcPostList` ViewComponent
 * (`PcPostList.cs`) and the `<wv-post-list>` Stencil web component. Renders a
 * hierarchical comment/post tree with parent → child grouping (one level of
 * nesting, matching the monolith's `CommentService.cs` line 71 TODO), with
 * create, reply, and author-only delete capabilities.
 *
 * Source mapping:
 *  - `PcPostList.cs`          — Data fetching, record-to-tree conversion,
 *                                relatedRecords resolution, Stencil component
 *                                props serialisation
 *  - `CommentService.cs`      — Create (id, body, parent_id, l_scope,
 *                                l_related_records), Delete (author ownership
 *                                validation + one-level cascade)
 *  - `ProjectController.cs`   — POST /create (enriched record with user
 *                                image/username), POST /delete (author-only)
 *
 * Key architectural decisions:
 *  - One-level nesting only — children of top-level comments are shown inline;
 *    deeper nesting is flattened (matching monolith behaviour)
 *  - Author-only delete — enforced server-side AND visually gated in the UI
 *    by comparing `currentUser.id` to `comment.created_by`
 *  - Tailwind CSS for all styling — zero Bootstrap, zero jQuery, zero Stencil
 *  - Lazy-loadable via default export for route-based code splitting
 */

import React, { useState } from 'react';
import type { FormEvent } from 'react';
import {
  useComments,
  useCreateComment,
  useDeleteComment,
} from '../../hooks/useProjects';
import { useAuthStore } from '../../stores/authStore';
import type { EntityRecord } from '../../types/record';
import { formatRelativeTime } from '../../utils/formatters';

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

/**
 * Props for the CommentList component.
 *
 * Mirrors the data that PcPostList.cs injected into the Stencil component:
 *  - `relatedRecordId` — the task/record being commented on (PcPostList.cs
 *    line 80: `relatedRecordId = (Guid)Record["id"]`)
 *  - `entityName` — optional entity context (e.g. "task")
 *  - `relatedRecords` — optional array of related record IDs (project IDs
 *    gathered via EQL join in PcPostList.cs lines 87-100)
 *  - `inline` — when true, renders in embedded mode (no standalone page
 *    chrome) for use inside TaskDetails
 */
interface CommentListProps {
  /** The record ID to show comments for (e.g. task ID). */
  relatedRecordId: string;
  /** Entity name context (e.g. "task"). */
  entityName?: string;
  /** Additional related record IDs (e.g. project IDs). */
  relatedRecords?: string[];
  /** Inline mode for embedding in TaskDetails. */
  inline?: boolean;
}

/**
 * Internal node type for the comment tree. Extends the flat comment record
 * with a strongly-typed `children` array representing one level of replies.
 */
interface CommentNode {
  id: string;
  body: string;
  parentId: string | null;
  createdBy: string;
  createdOn: string;
  userImage: string;
  userUsername: string;
  children: CommentNode[];
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/**
 * Extracts a typed comment node from a dynamic EntityRecord.
 *
 * Comment records from the API contain dynamic keys like `body`, `parent_id`,
 * `created_by`, `created_on`, `user_image`, `user_username`. This function
 * normalises them into a strongly-typed CommentNode.
 */
function toCommentNode(record: EntityRecord): CommentNode {
  return {
    id: String(record.id ?? ''),
    body: String(record.body ?? ''),
    parentId: record.parent_id ? String(record.parent_id) : null,
    createdBy: String(record.created_by ?? ''),
    createdOn: String(record.created_on ?? ''),
    userImage: String(record.user_image ?? ''),
    userUsername: String(record.user_username ?? ''),
    children: [],
  };
}

/**
 * Builds a one-level comment tree from a flat list of EntityRecords.
 *
 * Mirrors `PcPostList.cs` `ConvertRecordListToTree` which grouped comments by
 * `parent_id` and sorted by `created_on` ascending. Only one level of nesting
 * is supported — matching the monolith's CommentService.cs line 71 TODO about
 * limited nesting.
 *
 * Algorithm:
 *  1. Convert all records to CommentNode objects
 *  2. Separate top-level comments (no parent_id) from replies
 *  3. Attach replies as children of their parent
 *  4. Sort top-level comments by created_on ascending
 *  5. Sort children within each parent by created_on ascending
 */
function buildCommentTree(records: EntityRecord[]): CommentNode[] {
  const nodes = records.map(toCommentNode);

  const topLevel: CommentNode[] = [];
  const childMap = new Map<string, CommentNode[]>();

  for (const node of nodes) {
    if (!node.parentId) {
      topLevel.push(node);
    } else {
      const siblings = childMap.get(node.parentId) ?? [];
      siblings.push(node);
      childMap.set(node.parentId, siblings);
    }
  }

  /* Sort ascending by created_on (oldest first) matching monolith behaviour */
  const sortByCreatedOn = (a: CommentNode, b: CommentNode): number => {
    const dateA = new Date(a.createdOn).getTime() || 0;
    const dateB = new Date(b.createdOn).getTime() || 0;
    return dateA - dateB;
  };

  topLevel.sort(sortByCreatedOn);

  for (const parent of topLevel) {
    const children = childMap.get(parent.id) ?? [];
    children.sort(sortByCreatedOn);
    parent.children = children;
  }

  return topLevel;
}

/**
 * Generates initials from a username for use as an avatar fallback.
 *
 * Examples:
 *  - "John Doe"  → "JD"
 *  - "admin"     → "AD"
 *  - ""          → "?"
 */
function getInitials(name: string): string {
  const trimmed = name.trim();
  if (trimmed.length === 0) {
    return '?';
  }
  const parts = trimmed.split(/\s+/);
  if (parts.length >= 2) {
    return `${parts[0][0]}${parts[1][0]}`.toUpperCase();
  }
  return trimmed.substring(0, 2).toUpperCase();
}

// ---------------------------------------------------------------------------
// Sub-components
// ---------------------------------------------------------------------------

/**
 * User avatar circle (24×24px). Displays the user's profile image when
 * available, falling back to initials on a coloured background.
 *
 * Replaces the monolith's `$user_1n_comment.image` EQL join rendering.
 */
function UserAvatar({
  image,
  username,
}: {
  image: string;
  username: string;
}): React.JSX.Element {
  const hasImage = image.length > 0;

  if (hasImage) {
    return (
      <img
        src={image}
        alt=""
        aria-hidden="true"
        width={24}
        height={24}
        loading="lazy"
        decoding="async"
        className="inline-block size-6 shrink-0 rounded-full object-cover bg-gray-200"
      />
    );
  }

  return (
    <span
      aria-hidden="true"
      className="inline-flex size-6 shrink-0 items-center justify-center rounded-full bg-indigo-100 text-[0.625rem] font-medium leading-none text-indigo-700"
    >
      {getInitials(username)}
    </span>
  );
}

/**
 * Inline form for creating a new comment or reply.
 *
 * Renders a textarea and submit button. When `parentId` is set, the form
 * creates a reply (child comment); otherwise a top-level comment.
 */
function CommentForm({
  relatedRecordId,
  parentId,
  onSuccess,
  placeholder,
}: {
  relatedRecordId: string;
  parentId?: string;
  onSuccess?: () => void;
  placeholder?: string;
}): React.JSX.Element {
  const [body, setBody] = useState('');
  const createComment = useCreateComment();

  const handleSubmit = (e: FormEvent<HTMLFormElement>): void => {
    e.preventDefault();
    const trimmed = body.trim();
    if (trimmed.length === 0) {
      return;
    }

    const data: EntityRecord = { body: trimmed };
    if (parentId) {
      data.parent_id = parentId;
    }

    createComment.mutate(
      { taskId: relatedRecordId, data },
      {
        onSuccess: () => {
          setBody('');
          onSuccess?.();
        },
      },
    );
  };

  return (
    <form onSubmit={handleSubmit} className="flex flex-col gap-2">
      <label htmlFor={`comment-input-${parentId ?? 'top'}`} className="sr-only">
        {parentId ? 'Write a reply' : 'Write a comment'}
      </label>
      <textarea
        id={`comment-input-${parentId ?? 'top'}`}
        value={body}
        onChange={(e) => setBody(e.target.value)}
        placeholder={placeholder ?? 'Write a comment…'}
        rows={3}
        className="w-full resize-y rounded-md border border-gray-300 bg-white px-3 py-2 text-sm text-gray-900 placeholder:text-gray-400 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-[-1px] focus-visible:outline-indigo-600"
        disabled={createComment.isPending}
      />

      {createComment.isError && (
        <p role="alert" className="text-sm text-red-600">
          {createComment.error?.message ?? 'Failed to post comment.'}
        </p>
      )}

      <div className="flex justify-end">
        <button
          type="submit"
          disabled={createComment.isPending || body.trim().length === 0}
          className="inline-flex items-center rounded-md bg-indigo-600 px-3 py-1.5 text-sm font-semibold text-white shadow-sm hover:bg-indigo-500 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600 disabled:cursor-not-allowed disabled:opacity-50"
        >
          {createComment.isPending ? 'Posting…' : parentId ? 'Reply' : 'Post'}
        </button>
      </div>
    </form>
  );
}

/**
 * Single comment item with avatar, username, body, timestamp, reply button,
 * and author-only delete button.
 *
 * Replaces the per-item rendering in the Stencil `<wv-post-list>` component.
 */
function CommentItem({
  comment,
  relatedRecordId,
  currentUserId,
  isChild,
}: {
  comment: CommentNode;
  relatedRecordId: string;
  currentUserId: string;
  isChild?: boolean;
}): React.JSX.Element {
  const [showReply, setShowReply] = useState(false);
  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false);
  const deleteComment = useDeleteComment();

  const isAuthor = currentUserId.length > 0 && currentUserId === comment.createdBy;

  const handleDelete = (): void => {
    deleteComment.mutate(comment.id, {
      onSuccess: () => {
        setShowDeleteConfirm(false);
      },
    });
  };

  return (
    <article
      className={`flex gap-3 ${isChild ? 'ms-9 border-s-2 border-gray-100 ps-3' : ''}`}
      aria-label={`Comment by ${comment.userUsername}`}
    >
      <UserAvatar image={comment.userImage} username={comment.userUsername} />

      <div className="min-w-0 flex-1">
        {/* Header: username + timestamp */}
        <div className="flex flex-wrap items-baseline gap-x-2">
          <span className="text-sm font-medium text-gray-900">
            {comment.userUsername || 'Unknown User'}
          </span>
          <time
            dateTime={comment.createdOn}
            className="text-xs text-gray-500"
            title={comment.createdOn ? new Date(comment.createdOn).toLocaleString() : ''}
          >
            {comment.createdOn ? formatRelativeTime(comment.createdOn) : ''}
          </time>
        </div>

        {/* Body */}
        <div className="mt-1 text-sm text-gray-700 overflow-wrap-break-word">
          {comment.body}
        </div>

        {/* Actions: Reply + Delete */}
        <div className="mt-1.5 flex items-center gap-3">
          {/* Only top-level comments can receive replies (one-level nesting) */}
          {!isChild && (
            <button
              type="button"
              onClick={() => setShowReply((prev) => !prev)}
              className="text-xs font-medium text-gray-500 hover:text-indigo-600 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
            >
              {showReply ? 'Cancel' : 'Reply'}
            </button>
          )}

          {isAuthor && (
            <>
              {!showDeleteConfirm ? (
                <button
                  type="button"
                  onClick={() => setShowDeleteConfirm(true)}
                  className="text-xs font-medium text-gray-500 hover:text-red-600 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600"
                >
                  Delete
                </button>
              ) : (
                <span className="inline-flex items-center gap-2">
                  <span className="text-xs text-red-600">Delete this comment?</span>
                  <button
                    type="button"
                    onClick={handleDelete}
                    disabled={deleteComment.isPending}
                    className="text-xs font-semibold text-red-600 hover:text-red-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600 disabled:opacity-50"
                  >
                    {deleteComment.isPending ? 'Deleting…' : 'Confirm'}
                  </button>
                  <button
                    type="button"
                    onClick={() => setShowDeleteConfirm(false)}
                    className="text-xs font-medium text-gray-500 hover:text-gray-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-600"
                  >
                    Cancel
                  </button>
                </span>
              )}
            </>
          )}

          {deleteComment.isError && (
            <p role="alert" className="text-xs text-red-600">
              {deleteComment.error?.message ?? 'Failed to delete.'}
            </p>
          )}
        </div>

        {/* Inline reply form (only for top-level comments) */}
        {showReply && !isChild && (
          <div className="mt-2">
            <CommentForm
              relatedRecordId={relatedRecordId}
              parentId={comment.id}
              onSuccess={() => setShowReply(false)}
              placeholder="Write a reply…"
            />
          </div>
        )}
      </div>
    </article>
  );
}

// ---------------------------------------------------------------------------
// Main Component
// ---------------------------------------------------------------------------

/**
 * CommentList — Hierarchical comment/post tree with CRUD capabilities.
 *
 * Replaces the monolith's `PcPostList` ViewComponent + `<wv-post-list>`
 * Stencil web component. Renders top-level comments with one level of nested
 * child replies, a creation form, inline reply forms, and author-only delete
 * with confirmation.
 *
 * Data flow:
 *  1. `useComments(relatedRecordId)` fetches flat comment list from API
 *  2. `buildCommentTree()` groups into parent → children hierarchy
 *  3. Top-level creation form posts via `useCreateComment()`
 *  4. Per-comment reply form posts with `parent_id` via `useCreateComment()`
 *  5. Author-only delete triggers `useDeleteComment()` with confirmation
 */
function CommentList({
  relatedRecordId,
  entityName,
  relatedRecords,
  inline = false,
}: CommentListProps): React.JSX.Element {
  const currentUser = useAuthStore((state) => state.currentUser);
  const currentUserId = currentUser?.id ?? '';

  const {
    data,
    isLoading,
    isError,
    error,
  } = useComments(relatedRecordId);

  const records = data?.records ?? [];
  const commentTree = buildCommentTree(records);

  /* ------------------------------------------------------------------ */
  /* Loading state                                                      */
  /* ------------------------------------------------------------------ */
  if (isLoading) {
    return (
      <section
        aria-label="Comments"
        className={inline ? '' : 'mx-auto max-w-3xl px-4 py-6'}
        data-entity={entityName}
        data-related={relatedRecords?.join(',')}
      >
        <div className="flex items-center justify-center py-8">
          <svg
            className="size-6 animate-spin text-indigo-600"
            xmlns="http://www.w3.org/2000/svg"
            fill="none"
            viewBox="0 0 24 24"
            aria-hidden="true"
          >
            <circle
              className="opacity-25"
              cx="12"
              cy="12"
              r="10"
              stroke="currentColor"
              strokeWidth="4"
            />
            <path
              className="opacity-75"
              fill="currentColor"
              d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z"
            />
          </svg>
          <span className="ms-2 text-sm text-gray-500">Loading comments…</span>
        </div>
      </section>
    );
  }

  /* ------------------------------------------------------------------ */
  /* Error state                                                        */
  /* ------------------------------------------------------------------ */
  if (isError) {
    return (
      <section
        aria-label="Comments"
        className={inline ? '' : 'mx-auto max-w-3xl px-4 py-6'}
        data-entity={entityName}
        data-related={relatedRecords?.join(',')}
      >
        <div role="alert" className="rounded-md bg-red-50 p-4">
          <p className="text-sm font-medium text-red-800">
            {error?.message ?? 'Failed to load comments.'}
          </p>
        </div>
      </section>
    );
  }

  /* ------------------------------------------------------------------ */
  /* Rendered state (loaded successfully)                               */
  /* ------------------------------------------------------------------ */
  return (
    <section
      aria-label="Comments"
      className={inline ? '' : 'mx-auto max-w-3xl px-4 py-6'}
      data-entity={entityName}
      data-related={relatedRecords?.join(',')}
    >
      {/* Section heading (standalone mode only) */}
      {!inline && (
        <h2 className="mb-4 text-lg font-semibold text-gray-900">
          Comments
          {records.length > 0 && (
            <span className="ms-1.5 text-sm font-normal text-gray-500">
              ({records.length})
            </span>
          )}
        </h2>
      )}

      {/* Creation form (top-level) */}
      <div className={inline ? 'mb-4' : 'mb-6'}>
        <CommentForm
          relatedRecordId={relatedRecordId}
          placeholder="Write a comment…"
        />
      </div>

      {/* Empty state */}
      {commentTree.length === 0 && (
        <p className="py-6 text-center text-sm text-gray-500">
          No comments yet. Be the first to comment.
        </p>
      )}

      {/* Comment tree */}
      {commentTree.length > 0 && (
        <ul role="list" className="flex flex-col gap-4">
          {commentTree.map((parent) => (
            <li key={parent.id}>
              {/* Top-level comment */}
              <CommentItem
                comment={parent}
                relatedRecordId={relatedRecordId}
                currentUserId={currentUserId}
              />

              {/* Child replies (one level of nesting) */}
              {parent.children.length > 0 && (
                <ul role="list" className="mt-3 flex flex-col gap-3">
                  {parent.children.map((child) => (
                    <li key={child.id}>
                      <CommentItem
                        comment={child}
                        relatedRecordId={relatedRecordId}
                        currentUserId={currentUserId}
                        isChild
                      />
                    </li>
                  ))}
                </ul>
              )}
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

export default CommentList;
