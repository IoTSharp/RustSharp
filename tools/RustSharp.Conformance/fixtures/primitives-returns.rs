fn sum(a: i32, b: i32) -> i32 { a + b }
fn left() -> i32 { println!("left"); 3 }
fn args() -> i32 { sum(left(), { return 11; }) }
fn nested() -> i32 { left() + if false { return 9; } else { 4 } }
fn exits(flag: bool) -> i32 { if flag { return 1; } else { return 2; } }
fn main() {
    println!("{}", args());
    println!("{}", nested());
    println!("{}", exits(false));
}
