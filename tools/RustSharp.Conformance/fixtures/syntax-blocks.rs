fn f(flag: bool) -> i32 {
    let uninitialized: i32;
    let mut value = { let inner = 1; inner };
    { value = 2; }
    if flag { value = 3; } else if other { value = 4; }
    if flag { value } else { 0 }
}
