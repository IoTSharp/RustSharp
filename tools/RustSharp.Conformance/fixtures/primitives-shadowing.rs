fn fib(n: i32) -> i32 { if n < 2 { n } else { fib(n - 1) + fib(n - 2) } }
fn main() {
    let value = 7;
    let value = value + 1;
    { let value = true; println!("{}", value); }
    println!("{}", value);
    println!("{}", fib(8));
}
