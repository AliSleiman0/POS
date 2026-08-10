import { describe, expect, it } from 'vitest'
import { validateEmail, validateDisplayName, validateNewEmployee, validatePin } from './validation'

describe('validateDisplayName', () => {
  it('requires something other than whitespace', () => {
    expect(validateDisplayName('   ')).toBeDefined()
    expect(validateDisplayName('Robin Vale')).toBeUndefined()
  })

  it('mirrors the server length limit', () => {
    expect(validateDisplayName('a'.repeat(100))).toBeUndefined()
    expect(validateDisplayName('a'.repeat(101))).toBeDefined()
  })
})

describe('validateEmail', () => {
  it('accepts an ordinary address', () => {
    expect(validateEmail('robin@example.com')).toBeUndefined()
  })

  it('rejects one with no at-sign', () => {
    expect(validateEmail('robin.example.com')).toBeDefined()
  })

  // Deliberately shallow. Rejecting addresses that actually work is the
  // failure mode of clever email regexes, and the only real authority on
  // whether an address exists is whether mail reaches it.
  it.each(['a+tag@example.co.uk', "o'brien@example.com", 'x@y'])(
    'accepts the unusual but valid address %s',
    (email) => {
      expect(validateEmail(email)).toBeUndefined()
    },
  )
})

describe('validatePin', () => {
  it.each(['4821', '48210', '482105'])('accepts %s', (pin) => {
    expect(validatePin(pin)).toBeUndefined()
  })

  it.each(['123', '1234567', 'abcd', '12 34', '', '12.4'])('rejects %s', (pin) => {
    expect(validatePin(pin)).toBeDefined()
  })
})

describe('validateNewEmployee', () => {
  const valid = {
    displayName: 'Robin Vale',
    email: 'robin@example.com',
    password: 'Battery-Staple-7',
    pin: '',
  }

  it('passes a complete form with no PIN', () => {
    // A PIN is optional: somebody who does the books and never touches the till
    // should not be handed a working till credential.
    expect(validateNewEmployee(valid)).toEqual({})
  })

  it('passes a complete form with a PIN', () => {
    expect(validateNewEmployee({ ...valid, pin: '4821' })).toEqual({})
  })

  it('rejects a malformed PIN only when one was typed', () => {
    expect(validateNewEmployee({ ...valid, pin: '12' })).toHaveProperty('pin')
  })

  it('reports emptiness but leaves the password rules to the server', () => {
    expect(validateNewEmployee({ ...valid, password: '' })).toHaveProperty('password')

    // "short" breaks Identity's length rule, and the server says so with the
    // rule named. Duplicating that here would go stale the first time the
    // option changes and leave the form confidently wrong.
    expect(validateNewEmployee({ ...valid, password: 'short' })).toEqual({})
  })

  it('collects every problem at once rather than one at a time', () => {
    const errors = validateNewEmployee({
      displayName: '',
      email: 'nope',
      password: '',
      pin: 'x',
    })

    expect(Object.keys(errors).sort()).toEqual(['displayName', 'email', 'password', 'pin'])
  })
})
