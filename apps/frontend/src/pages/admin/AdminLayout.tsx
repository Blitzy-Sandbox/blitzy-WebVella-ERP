/**
 * AdminLayout — Admin Console Layout Wrapper
 *
 * Provides section navigation links for the admin console.
 * Wraps all admin routes with a consistent navigation bar
 * containing links to Entities, Roles, Users, and other admin sections.
 *
 * Replaces the monolith's SDK plugin sidebar navigation structure.
 */

import { Outlet, NavLink } from 'react-router-dom';

/** Admin section navigation items */
const ADMIN_NAV_ITEMS = [
  { to: '/admin/entities', label: 'Entities', testId: 'admin-nav-entities' },
  { to: '/admin/roles', label: 'Roles', testId: 'admin-nav-roles' },
  { to: '/admin/users', label: 'Users', testId: 'admin-nav-users' },
  { to: '/admin/applications', label: 'Applications', testId: 'admin-nav-applications' },
  { to: '/admin/data-sources', label: 'Data Sources', testId: 'admin-nav-datasources' },
  { to: '/admin/pages', label: 'Pages', testId: 'admin-nav-pages' },
  { to: '/admin/jobs', label: 'Jobs', testId: 'admin-nav-jobs' },
  { to: '/admin/logs', label: 'Logs', testId: 'admin-nav-logs' },
];

export default function AdminLayout() {
  return (
    <div className="min-h-full">
      {/* Admin section sub-navigation */}
      <nav
        className="mb-6 border-b border-gray-200 bg-white"
        aria-label="Admin sections"
        data-testid="admin-section-nav"
      >
        <div className="flex space-x-1 overflow-x-auto px-4 py-2">
          {ADMIN_NAV_ITEMS.map((item) => (
            <NavLink
              key={item.to}
              to={item.to}
              data-testid={item.testId}
              className={({ isActive }) =>
                `whitespace-nowrap rounded-md px-3 py-2 text-sm font-medium transition-colors ${
                  isActive
                    ? 'bg-blue-50 text-blue-700'
                    : 'text-gray-600 hover:bg-gray-100 hover:text-gray-900'
                }`
              }
            >
              {item.label}
            </NavLink>
          ))}
        </div>
      </nav>

      {/* Render the matched child route */}
      <Outlet />
    </div>
  );
}
