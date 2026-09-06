fn f() {
    a | b == c & d;
    a ^ b != c;
    a << b + c * d;
    a >> 1; a >>= 2;
    a = b = c;
    a += b; a -= b; a *= b; a /= b; a %= b;
    a ^= b; a &= b; a |= b; a <<= b;
    a && b || c;
    a <= b; a >= b; (a < b) == c;
    !a; -a * b; *a; &a; &&mut a;
}
