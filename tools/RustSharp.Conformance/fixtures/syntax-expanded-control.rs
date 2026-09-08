fn f() {
    'outer: loop { for (x, y) in 0..3 { if x == y { continue 'outer; } } break 'outer 4; }
    while let Some(x) = source { break; }
    if let Some(y) = source && y > 0 && let Some(z) = next { z; }
    'scope: { break 'scope 2; };
    const { 1 + 2 };
    call(return 1);
}
