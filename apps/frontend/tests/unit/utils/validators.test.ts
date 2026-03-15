/**
 * Vitest Tests for Input Validator Functions
 *
 * Comprehensive test suite for `apps/frontend/src/utils/validators.ts`.
 * Validates replacement of monolith validation logic:
 *   - TextExtensions.IsEmail() from WebVella.Erp/Utilities/TextExtensions.cs (lines 41-52)
 *   - Guid.TryParse behavior for UUID validation
 *   - PcFieldBaseOptions min/max/maxlength constraint validation
 *   - PasswordField min_length/max_length complexity validation
 *   - PhoneField, UrlField validators for the 21 field types from FieldType.cs
 *
 * Rules:
 *   - Pure function testing only — zero DOM rendering, zero React components, zero mocking
 *   - All imports are named imports
 *   - Full behavioral parity with monolith validation paths
 */

import { describe, it, expect } from 'vitest';
import {
  isValidEmail,
  isValidGuid,
  isValidUrl,
  isValidPhone,
  isInRange,
  isWithinLength,
  isRequired,
  isStrongPassword,
} from '../../../src/utils/validators';

// =============================================================================
// isValidEmail — Replacement for TextExtensions.IsEmail() (TextExtensions.cs:41-52)
// =============================================================================

describe('isValidEmail', () => {
  describe('valid email addresses', () => {
    it('should accept a simple valid email', () => {
      expect(isValidEmail('user@example.com')).toBe(true);
    });

    it('should accept an email with a subdomain', () => {
      expect(isValidEmail('user@mail.example.com')).toBe(true);
    });

    it('should accept an email with a plus tag', () => {
      expect(isValidEmail('user+tag@example.com')).toBe(true);
    });

    it('should accept an email with dots in the local part', () => {
      expect(isValidEmail('first.last@example.com')).toBe(true);
    });

    it('should accept an email with numbers in the local part', () => {
      expect(isValidEmail('user123@example.com')).toBe(true);
    });

    it('should accept an email with special characters in local part', () => {
      expect(isValidEmail("user!def#abc$xyz%qrs&uvw@example.com")).toBe(true);
    });

    it('should accept an email with hyphenated domain', () => {
      expect(isValidEmail('user@my-domain.com')).toBe(true);
    });

    it('should accept an email with numeric domain segments', () => {
      expect(isValidEmail('user@123.123.123.com')).toBe(true);
    });
  });

  describe('invalid email addresses', () => {
    it('should reject an empty string', () => {
      expect(isValidEmail('')).toBe(false);
    });

    it('should reject null', () => {
      expect(isValidEmail(null as any)).toBe(false);
    });

    it('should reject undefined', () => {
      expect(isValidEmail(undefined as any)).toBe(false);
    });

    it('should reject a string with no @ symbol', () => {
      expect(isValidEmail('userexample.com')).toBe(false);
    });

    it('should reject a string with no domain', () => {
      expect(isValidEmail('user@')).toBe(false);
    });

    it('should reject a string with no local part', () => {
      expect(isValidEmail('@example.com')).toBe(false);
    });

    it('should reject a string with spaces in the local part', () => {
      expect(isValidEmail('user @example.com')).toBe(false);
    });

    it('should reject a double @@ symbol', () => {
      expect(isValidEmail('user@@example.com')).toBe(false);
    });

    it('should reject plain text without email structure', () => {
      expect(isValidEmail('notanemail')).toBe(false);
    });

    it('should reject a whitespace-only string', () => {
      expect(isValidEmail('   ')).toBe(false);
    });

    it('should reject a numeric value passed as any', () => {
      expect(isValidEmail(12345 as any)).toBe(false);
    });

    it('should reject a boolean value passed as any', () => {
      expect(isValidEmail(true as any)).toBe(false);
    });
  });
});

// =============================================================================
// isValidGuid — Replacement for Guid.TryParse (Definitions.cs SystemIds)
// =============================================================================

describe('isValidGuid', () => {
  describe('valid GUIDs', () => {
    it('should accept a lowercase GUID (SystemEntityId from Definitions.cs line 8)', () => {
      expect(isValidGuid('a5050ac8-5967-4ce1-95e7-a79b054f9d14')).toBe(true);
    });

    it('should accept an uppercase GUID (AdministratorRoleId from Definitions.cs line 15)', () => {
      expect(isValidGuid('BDC56420-CAF0-4030-8A0E-D264938E0CDA')).toBe(true);
    });

    it('should accept the SystemUserId GUID (Definitions.cs line 19)', () => {
      expect(isValidGuid('10000000-0000-0000-0000-000000000000')).toBe(true);
    });

    it('should accept the empty/zero GUID', () => {
      expect(isValidGuid('00000000-0000-0000-0000-000000000000')).toBe(true);
    });

    it('should accept the UserEntityId GUID (Definitions.cs line 9)', () => {
      expect(isValidGuid('b9cebc3b-6443-452a-8e34-b311a73dcc8b')).toBe(true);
    });

    it('should accept the RoleEntityId GUID (Definitions.cs line 10)', () => {
      expect(isValidGuid('c4541fee-fbb6-4661-929e-1724adec285a')).toBe(true);
    });

    it('should accept the FirstUserId GUID (Definitions.cs line 20)', () => {
      expect(isValidGuid('EABD66FD-8DE1-4D79-9674-447EE89921C2')).toBe(true);
    });

    it('should accept mixed-case GUID', () => {
      expect(isValidGuid('AbCd1234-5678-9AbC-dEfG-000000000000'.replace(/G/gi, 'a'))).toBe(true);
    });

    it('should accept a GUID with leading/trailing whitespace (trimmed)', () => {
      expect(isValidGuid('  a5050ac8-5967-4ce1-95e7-a79b054f9d14  ')).toBe(true);
    });
  });

  describe('invalid GUIDs', () => {
    it('should reject a truncated GUID (too short)', () => {
      expect(isValidGuid('a5050ac8-5967-4ce1-95e7')).toBe(false);
    });

    it('should reject a GUID without dashes', () => {
      expect(isValidGuid('a5050ac859674ce195e7a79b054f9d14')).toBe(false);
    });

    it('should reject an empty string', () => {
      expect(isValidGuid('')).toBe(false);
    });

    it('should reject null', () => {
      expect(isValidGuid(null as any)).toBe(false);
    });

    it('should reject undefined', () => {
      expect(isValidGuid(undefined as any)).toBe(false);
    });

    it('should reject random text', () => {
      expect(isValidGuid('not-a-guid')).toBe(false);
    });

    it('should reject a GUID with invalid hex character g', () => {
      expect(isValidGuid('g5050ac8-5967-4ce1-95e7-a79b054f9d14')).toBe(false);
    });

    it('should reject a GUID with extra characters appended', () => {
      expect(isValidGuid('a5050ac8-5967-4ce1-95e7-a79b054f9d14-extra')).toBe(false);
    });

    it('should reject a GUID with braces (non-standard format)', () => {
      expect(isValidGuid('{a5050ac8-5967-4ce1-95e7-a79b054f9d14}')).toBe(false);
    });

    it('should reject a numeric value passed as any', () => {
      expect(isValidGuid(12345 as any)).toBe(false);
    });
  });
});

// =============================================================================
// isValidUrl — UrlField validation (FieldType.cs: UrlField = 19)
// =============================================================================

describe('isValidUrl', () => {
  describe('valid URLs', () => {
    it('should accept a valid HTTP URL', () => {
      expect(isValidUrl('http://example.com')).toBe(true);
    });

    it('should accept a valid HTTPS URL', () => {
      expect(isValidUrl('https://example.com')).toBe(true);
    });

    it('should accept a URL with a path', () => {
      expect(isValidUrl('https://example.com/path/to/resource')).toBe(true);
    });

    it('should accept a URL with query parameters', () => {
      expect(isValidUrl('https://example.com?key=value')).toBe(true);
    });

    it('should accept a URL with a port (LocalStack endpoint)', () => {
      expect(isValidUrl('http://localhost:4566')).toBe(true);
    });

    it('should accept a URL with a hash fragment', () => {
      expect(isValidUrl('https://example.com/page#section')).toBe(true);
    });

    it('should accept a URL with complex query string', () => {
      expect(isValidUrl('https://example.com/api/v1?a=1&b=2&c=3')).toBe(true);
    });

    it('should accept an FTP URL', () => {
      expect(isValidUrl('ftp://files.example.com/data')).toBe(true);
    });
  });

  describe('invalid URLs', () => {
    it('should reject an empty string', () => {
      expect(isValidUrl('')).toBe(false);
    });

    it('should reject null', () => {
      expect(isValidUrl(null as any)).toBe(false);
    });

    it('should reject undefined', () => {
      expect(isValidUrl(undefined as any)).toBe(false);
    });

    it('should reject a URL without protocol', () => {
      expect(isValidUrl('example.com')).toBe(false);
    });

    it('should reject plain text', () => {
      expect(isValidUrl('not a url')).toBe(false);
    });

    it('should reject a whitespace-only string', () => {
      expect(isValidUrl('   ')).toBe(false);
    });

    it('should reject a mailto URL (non-http/https/ftp)', () => {
      expect(isValidUrl('mailto:user@example.com')).toBe(false);
    });

    it('should reject a javascript protocol URL', () => {
      expect(isValidUrl('javascript:alert(1)')).toBe(false);
    });

    it('should reject a numeric value passed as any', () => {
      expect(isValidUrl(12345 as any)).toBe(false);
    });
  });
});

// =============================================================================
// isValidPhone — PhoneField validation (FieldType.cs: PhoneField = 15)
// =============================================================================

describe('isValidPhone', () => {
  describe('valid phone numbers', () => {
    it('should accept digits only', () => {
      expect(isValidPhone('1234567890')).toBe(true);
    });

    it('should accept a phone number with dashes', () => {
      expect(isValidPhone('123-456-7890')).toBe(true);
    });

    it('should accept an international format with plus sign', () => {
      expect(isValidPhone('+1-555-123-4567')).toBe(true);
    });

    it('should accept a phone number with parentheses', () => {
      expect(isValidPhone('(555) 123-4567')).toBe(true);
    });

    it('should accept a phone number with spaces', () => {
      expect(isValidPhone('123 456 7890')).toBe(true);
    });

    it('should accept a phone number with dots', () => {
      expect(isValidPhone('555.123.4567')).toBe(true);
    });

    it('should accept a short digit sequence (regex allows any length)', () => {
      expect(isValidPhone('123')).toBe(true);
    });

    it('should accept a single digit', () => {
      expect(isValidPhone('5')).toBe(true);
    });

    it('should accept a long international number', () => {
      expect(isValidPhone('+44 20 7946 0958')).toBe(true);
    });
  });

  describe('invalid phone numbers', () => {
    it('should reject an empty string', () => {
      expect(isValidPhone('')).toBe(false);
    });

    it('should reject null', () => {
      expect(isValidPhone(null as any)).toBe(false);
    });

    it('should reject undefined', () => {
      expect(isValidPhone(undefined as any)).toBe(false);
    });

    it('should reject alphabetic characters', () => {
      expect(isValidPhone('abcdefghij')).toBe(false);
    });

    it('should reject mixed alpha-numeric text', () => {
      expect(isValidPhone('call-me-123')).toBe(false);
    });

    it('should reject a whitespace-only string', () => {
      expect(isValidPhone('   ')).toBe(false);
    });

    it('should reject special characters not in the phone pattern', () => {
      expect(isValidPhone('!@#$%^&*')).toBe(false);
    });

    it('should reject a numeric value passed as any', () => {
      expect(isValidPhone(12345 as any)).toBe(false);
    });
  });
});

// =============================================================================
// isInRange — PcFieldBaseOptions min/max constraints (PcFieldBase.cs line 117+)
// =============================================================================

describe('isInRange', () => {
  describe('within range', () => {
    it('should return true for a value within the range', () => {
      expect(isInRange(5, 0, 10)).toBe(true);
    });

    it('should return true for value at the minimum boundary (inclusive)', () => {
      expect(isInRange(0, 0, 10)).toBe(true);
    });

    it('should return true for value at the maximum boundary (inclusive)', () => {
      expect(isInRange(10, 0, 10)).toBe(true);
    });

    it('should return true for a negative value within a negative range', () => {
      expect(isInRange(-5, -10, -1)).toBe(true);
    });

    it('should return true for a decimal value within the range', () => {
      expect(isInRange(5.5, 0, 10)).toBe(true);
    });

    it('should return true for zero in a range spanning negative to positive', () => {
      expect(isInRange(0, -100, 100)).toBe(true);
    });
  });

  describe('out of range', () => {
    it('should return false for a value below the minimum', () => {
      expect(isInRange(-1, 0, 10)).toBe(false);
    });

    it('should return false for a value above the maximum', () => {
      expect(isInRange(11, 0, 10)).toBe(false);
    });

    it('should return false for a large negative value below a negative range', () => {
      expect(isInRange(-20, -10, -1)).toBe(false);
    });
  });

  describe('edge cases', () => {
    it('should return false for null value (non-finite)', () => {
      expect(isInRange(null as any, 0, 10)).toBe(false);
    });

    it('should return false for undefined value (non-finite)', () => {
      expect(isInRange(undefined as any, 0, 10)).toBe(false);
    });

    it('should return false for NaN', () => {
      expect(isInRange(NaN, 0, 10)).toBe(false);
    });

    it('should return false for Infinity', () => {
      expect(isInRange(Infinity, 0, 10)).toBe(false);
    });

    it('should return false for negative Infinity', () => {
      expect(isInRange(-Infinity, -100, 100)).toBe(false);
    });

    it('should validate only max when min is undefined', () => {
      expect(isInRange(5, undefined, 10)).toBe(true);
    });

    it('should validate only min when max is undefined', () => {
      expect(isInRange(5, 0, undefined)).toBe(true);
    });

    it('should return true when both min and max are undefined (no bounds)', () => {
      expect(isInRange(999, undefined, undefined)).toBe(true);
    });

    it('should validate only max correctly and reject when exceeded', () => {
      expect(isInRange(15, undefined, 10)).toBe(false);
    });

    it('should validate only min correctly and reject when below', () => {
      expect(isInRange(-5, 0, undefined)).toBe(false);
    });

    it('should return true when min equals max and value matches', () => {
      expect(isInRange(5, 5, 5)).toBe(true);
    });

    it('should return false when min equals max and value differs', () => {
      expect(isInRange(6, 5, 5)).toBe(false);
    });
  });
});

// =============================================================================
// isWithinLength — PcFieldBaseOptions.maxlength (TextField default 200)
// =============================================================================

describe('isWithinLength', () => {
  describe('within length', () => {
    it('should return true when string is shorter than maxLength', () => {
      expect(isWithinLength('hello', 10)).toBe(true);
    });

    it('should return true when string is exactly at maxLength', () => {
      expect(isWithinLength('hello', 5)).toBe(true);
    });

    it('should return true for an empty string with positive maxLength', () => {
      expect(isWithinLength('', 10)).toBe(true);
    });

    it('should return true for an empty string with maxLength of 0', () => {
      expect(isWithinLength('', 0)).toBe(true);
    });

    it('should return true for a string at the default TextField maxLength (200)', () => {
      expect(isWithinLength('a'.repeat(200), 200)).toBe(true);
    });
  });

  describe('exceeding length', () => {
    it('should return false when string exceeds maxLength', () => {
      expect(isWithinLength('hello world', 5)).toBe(false);
    });

    it('should return false when string is one character over maxLength', () => {
      expect(isWithinLength('abcdef', 5)).toBe(false);
    });

    it('should return false for any non-empty string when maxLength is 0', () => {
      expect(isWithinLength('a', 0)).toBe(false);
    });

    it('should return false when string exceeds default TextField maxLength (200)', () => {
      expect(isWithinLength('a'.repeat(201), 200)).toBe(false);
    });
  });

  describe('edge cases', () => {
    it('should return false for null value (typeof check fails)', () => {
      expect(isWithinLength(null as any, 10)).toBe(false);
    });

    it('should return false for undefined value (typeof check fails)', () => {
      expect(isWithinLength(undefined as any, 10)).toBe(false);
    });

    it('should return false for a numeric value passed as any', () => {
      expect(isWithinLength(12345 as any, 10)).toBe(false);
    });

    it('should return false for a boolean value passed as any', () => {
      expect(isWithinLength(true as any, 10)).toBe(false);
    });

    it('should handle unicode characters by code unit length', () => {
      expect(isWithinLength('😀😀', 4)).toBe(true);
    });
  });
});

// =============================================================================
// isRequired — Required field validation for form submission
// =============================================================================

describe('isRequired', () => {
  describe('present values', () => {
    it('should return true for a non-empty string', () => {
      expect(isRequired('hello')).toBe(true);
    });

    it('should return true for a single character string', () => {
      expect(isRequired('a')).toBe(true);
    });

    it('should return true for number zero (zero IS a valid value)', () => {
      expect(isRequired(0)).toBe(true);
    });

    it('should return true for a positive number', () => {
      expect(isRequired(42)).toBe(true);
    });

    it('should return true for a negative number', () => {
      expect(isRequired(-1)).toBe(true);
    });

    it('should return true for boolean false (false IS a valid value)', () => {
      expect(isRequired(false)).toBe(true);
    });

    it('should return true for boolean true', () => {
      expect(isRequired(true)).toBe(true);
    });

    it('should return true for a non-empty array', () => {
      expect(isRequired(['a'])).toBe(true);
    });

    it('should return true for an array with multiple elements', () => {
      expect(isRequired([1, 2, 3])).toBe(true);
    });

    it('should return true for an object', () => {
      expect(isRequired({ key: 'value' })).toBe(true);
    });
  });

  describe('absent values', () => {
    it('should return false for an empty string', () => {
      expect(isRequired('')).toBe(false);
    });

    it('should return false for null', () => {
      expect(isRequired(null)).toBe(false);
    });

    it('should return false for undefined', () => {
      expect(isRequired(undefined)).toBe(false);
    });

    it('should return false for a whitespace-only string (trimmed check)', () => {
      expect(isRequired('   ')).toBe(false);
    });

    it('should return false for a string of tabs and newlines', () => {
      expect(isRequired('\t\n  ')).toBe(false);
    });

    it('should return false for an empty array', () => {
      expect(isRequired([])).toBe(false);
    });
  });
});

// =============================================================================
// isStrongPassword — PasswordField complexity validation (PasswordField.cs)
// Requires: >= 8 chars, uppercase, lowercase, digit, and special character
// =============================================================================

describe('isStrongPassword', () => {
  describe('valid strong passwords', () => {
    it('should accept a password meeting all complexity requirements', () => {
      expect(isStrongPassword('SecureP@ss1')).toBe(true);
    });

    it('should accept a password at the minimum length of 8 with all required types', () => {
      expect(isStrongPassword('Ab1!cdef')).toBe(true);
    });

    it('should accept a long password meeting all requirements', () => {
      const long = 'A' + 'a'.repeat(45) + '1@bb';
      expect(isStrongPassword(long)).toBe(true);
    });

    it('should accept a password with various special characters', () => {
      expect(isStrongPassword('Password1#$%')).toBe(true);
    });

    it('should accept a password with the minimum character set', () => {
      expect(isStrongPassword('pAssw0rd!')).toBe(true);
    });
  });

  describe('invalid passwords', () => {
    it('should reject a password shorter than 8 characters', () => {
      expect(isStrongPassword('Ab1!')).toBe(false);
    });

    it('should reject a password of 7 characters with all types', () => {
      expect(isStrongPassword('Ab1!xyz')).toBe(false);
    });

    it('should reject a password missing an uppercase letter', () => {
      expect(isStrongPassword('secure1@pass')).toBe(false);
    });

    it('should reject a password missing a lowercase letter', () => {
      expect(isStrongPassword('SECURE1@PASS')).toBe(false);
    });

    it('should reject a password missing a digit', () => {
      expect(isStrongPassword('SecureP@ss!')).toBe(false);
    });

    it('should reject a password missing a special character', () => {
      expect(isStrongPassword('SecurePass1')).toBe(false);
    });

    it('should reject a digits-only password of sufficient length', () => {
      expect(isStrongPassword('12345678')).toBe(false);
    });

    it('should reject a lowercase-only password of sufficient length', () => {
      expect(isStrongPassword('abcdefgh')).toBe(false);
    });

    it('should reject an uppercase-only password of sufficient length', () => {
      expect(isStrongPassword('ABCDEFGH')).toBe(false);
    });

    it('should reject an all-lowercase repeated string (missing upper, digit, special)', () => {
      expect(isStrongPassword('a'.repeat(50))).toBe(false);
    });

    it('should reject an empty string', () => {
      expect(isStrongPassword('')).toBe(false);
    });

    it('should reject null', () => {
      expect(isStrongPassword(null as any)).toBe(false);
    });

    it('should reject undefined', () => {
      expect(isStrongPassword(undefined as any)).toBe(false);
    });

    it('should reject a common weak password', () => {
      expect(isStrongPassword('password')).toBe(false);
    });

    it('should reject a numeric value passed as any', () => {
      expect(isStrongPassword(12345678 as any)).toBe(false);
    });
  });
});
